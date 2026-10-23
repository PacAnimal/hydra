using System.Security.Cryptography;
using Cathedral.Extensions;
using Hydra.Relay;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Tests.Setup;
using static Tests.Setup.ClipboardStreaming;

namespace Tests.Relay.Edge;

/// <summary>
/// Transport: clipboard streams beside file transfers on the bulk lane, several hosts at once, slow streams, aborts,
/// whole messages racing a stream, a restarted sender, and chunks out of order. Lane ordering runs over the in-memory Styx;
/// the rest over the fakes.
/// </summary>
[TestFixture]
public class ClipboardEdgeTransportTests : StyxFixtureBase
{
    private const string Target = "receiver";
    private const int FileChunkBytes = 64 * 1024;

    private readonly SlaveRelays _slaves = new();
    private FakeClipboardSync _clipboard = null!;
    private RecordingOsd _osd = null!;
    private FakeTimeProvider _clock = null!;

    [SetUp]
    public void SetUp()
    {
        _clipboard = new FakeClipboardSync();
        _osd = new RecordingOsd();
        _clock = new FakeTimeProvider();
    }

    [TearDown]
    public Task TearDown() => _slaves.StopAll();

    // -- 1, 2: a file transfer and a clipboard stream on the bulk lane --

    /// <summary>
    /// A file transfer and a clipboard stream from one sender to one receiver share the bulk lane: both arrive whole, each
    /// in its own order, and a key sent while both are part way out overtakes everything still held back.
    /// </summary>
    [Test]
    public async Task FileAndClipboardShareTheBulkLane_BothCompleteInOrder_AndAKeyOvertakesTheRest()
    {
        const int parkAt = 6;
        await using var pair = await ConnectedPair(receiver: Target);
        var (sender, receiver) = pair;
        var relay = new BulkPacedRelay(sender, parkAt);
        await using var stream = new ClipboardStreamSender(relay, () => Target, NullLogger.Instance);
        var file = FileBytes(1);
        var push = new ClipboardPushMessage(LargeText(2, 2 * 1024 * 1024));

        var transfer = Task.Run(() => SendFile(relay, Target, file));
        stream.Send(Target, MessageKind.ClipboardPush, push);
        await relay.Parked.WaitAsync(Bound);

        sender.SendKeyEvent([Target], TestMessages.KeyMessage(0));

        var arrivals = new Arrivals();
        while (await receiver.WaitForMessage("the keystroke (if it never comes, it is behind the bulk)") is var message && !IsInput(message))
            arrivals.Add(message);
        var bulkBeforeKey = arrivals.Bulk;

        relay.Gate.Release();
        await transfer.WaitAsync(Bound);
        while (!arrivals.FileDone || !arrivals.ClipboardDone)
            arrivals.Add(await receiver.WaitForMessage("the rest of the file and the clipboard"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bulkBeforeKey, Is.LessThanOrEqualTo(parkAt), "only the bulk already let through may come before the key");
            Assert.That(arrivals.Bulk, Is.GreaterThan(parkAt + 1), "pre-condition: bulk was still held back when the key went");
            AssertFileIntact(arrivals, file);
            AssertClipboardIntact(arrivals, push);
        }
    }

    /// <summary>A file one way and a clipboard stream the other, at once: both arrive whole.</summary>
    [Test]
    public async Task FileOneWayAndClipboardTheOther_AtOnce_BothComplete()
    {
        await using var pair = await ConnectedPair("a", "b");
        var (a, b) = pair;
        await using var stream = new ClipboardStreamSender(b, () => "a", NullLogger.Instance);
        var file = FileBytes(3);
        var push = new ClipboardPushMessage(LargeText(4, 2 * 1024 * 1024));

        var transfer = Task.Run(() => SendFile(a, "b", file));
        stream.Send("a", MessageKind.ClipboardPush, push);
        var atB = Collect(b, x => x.FileDone);
        var atA = Collect(a, x => x.ClipboardDone);
        await Task.WhenAll(transfer, atB, atA).WaitAsync(Bound);

        using (Assert.EnterMultipleScope())
        {
            AssertFileIntact(await atB, file);
            AssertClipboardIntact(await atA, push);
        }
    }

    // -- 3: three hosts, priority switching every chunk --

    /// <summary>
    /// Streams to three hosts at once, the cursor's host changing before every step: each step goes to the cursor's host
    /// while it still has something to send, and every stream lands whole and in order.
    /// </summary>
    [Test]
    public async Task ThreeHostsWithPrioritySwitchingEveryChunk_TheCursorsHostGoesNext_AndEveryStreamCompletes()
    {
        var relay = new FakeRelay();
        string[] hosts = ["a", "b", "c"];
        var switching = new TaskCompletionSource();
        var turn = 0;
        List<Pick> picks = [];
        string? Priority()
        {
            if (!switching.Task.IsCompleted) return null;
            var host = hosts[turn++ % hosts.Length];
            lock (picks) picks.Add(new Pick(host, StreamSends(relay).Count));
            return host;
        }
        await using var sender = new ClipboardStreamSender(relay, Priority, NullLogger.Instance);
        // different sizes, so they finish apart and the turns fall back to the rest
        var pushes = hosts.Select((_, i) => new ClipboardPushMessage(LargeText(10 + i, 600_000 + i * 350_000))).ToArray();
        var blobs = pushes.Select(p => MessageSerializer.Encode(MessageKind.ClipboardPush, p)).ToArray();

        // the first Begin parks on the gate, so all three are queued before the first switch
        relay.Reliable.Hold();
        sender.Send(hosts[0], MessageKind.ClipboardPush, pushes[0]);
        await relay.Sent.WaitFor(MessageKind.ClipboardStreamBegin, 1, hosts[0]);
        sender.Send(hosts[1], MessageKind.ClipboardPush, pushes[1]);
        sender.Send(hosts[2], MessageKind.ClipboardPush, pushes[2]);
        switching.SetResult();
        relay.Reliable.Release();
        for (var i = 0; i < hosts.Length; i++)
            await relay.Sent.WaitFor(MessageKind.ClipboardStreamChunk, ChunkCount(blobs[i]), hosts[i]);

        var sends = StreamSends(relay);
        List<Pick> taken;
        lock (picks) taken = [.. picks];
        var steps = hosts.ToDictionary(h => h, h => 1 + ChunkCount(blobs[Array.IndexOf(hosts, h)]));
        List<string> overtaken = [];
        var honoured = 0;
        for (var k = 1; k < sends.Count; k++)
        {
            // the last ask before step k is the one that chose it
            var priority = taken.Last(p => p.Sent == k).Priority;
            if (sends.Take(k).Count(s => s.Targets.Single() == priority) >= steps[priority]) continue;
            if (sends[k].Targets.Single() == priority) honoured++;
            else overtaken.Add($"step {k} went to {sends[k].Targets.Single()}, the cursor was on {priority}");
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(overtaken, Is.Empty);
            Assert.That(honoured, Is.GreaterThan(hosts.Length), "pre-condition: the cursor's host had steps to take");
            foreach (var (host, blob) in hosts.Zip(blobs))
                AssertStreamInOrder([.. sends.Where(s => s.Targets.Single() == host)], blob, host);
        }
    }

    // -- 4: slow but alive --

    [Test]
    public async Task ChunksJustInsideTheWatchdog_NeverTripIt_AndTheClipboardLands()
    {
        var slave = Slave();
        var text = BigText('a');
        var stream = StreamedPush(1, new ClipboardPushMessage(text));
        var almost = ClipboardStreamReceiver.Watchdog - TimeSpan.FromSeconds(1);

        await Begin(slave, stream);
        foreach (var chunk in stream.Chunks)
        {
            _clock.Advance(almost);
            await Chunk(slave, chunk);
        }
        // a stream still pending would time out here
        _clock.Advance(ClipboardStreamReceiver.Watchdog * 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_clipboard.Text, Is.EqualTo(text));
            Assert.That(_osd.Shown.Count, Is.Zero);
            Assert.That(slave.Sent.Of(MessageKind.ClipboardStreamAbort), Is.Empty);
        }
    }

    [Test]
    public async Task AGapJustPastTheWatchdog_TripsIt_FailureShown_SenderAborted_NothingApplied()
    {
        _clipboard.SetupText("old on the slave");
        var slave = Slave();
        var stream = StreamedPush(1, new ClipboardPushMessage(BigText('a')));
        var almost = ClipboardStreamReceiver.Watchdog - TimeSpan.FromSeconds(1);
        await Begin(slave, stream);
        _clock.Advance(almost);
        await Chunk(slave, stream.Chunks[0]);

        _clock.Advance(almost);
        var shownJustInside = _osd.Shown.Count;
        _clock.Advance(TimeSpan.FromSeconds(2));
        var failure = await _osd.Shown.Next("the failure");
        await Chunks(slave, stream, from: 1);
        _clock.Advance(ClipboardStreamReceiver.Watchdog * 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(shownJustInside, Is.Zero, "just inside the watchdog without a chunk is still alive");
            Assert.That(failure, Is.EqualTo(ClipboardStreamReceiver.FailedOsd));
            Assert.That(_osd.Shown.Count, Is.Zero, "the failure is shown once");
            Assert.That(AbortsTo(slave.Sent, Master), Is.EqualTo([1L]),
                "the sender is told to stop, once");
            Assert.That(_clipboard.SetClipboardCallCount, Is.Zero);
            Assert.That(_clipboard.Text, Is.Null, "an honest empty, never the old content");
        }
    }

    // -- 5: chunks after an abort, and strangers' chunks --

    [Test]
    public async Task ChunksAfterTheSendersAbort_ApplyNothing_AndShowNothing()
    {
        var slave = Slave();
        var stream = StreamedPush(4, new ClipboardPushMessage(BigText('a')));
        await Begin(slave, stream);
        await Chunk(slave, stream.Chunks[0]);

        await Abort(slave, 4);
        await Chunks(slave, stream, from: 1);
        _clock.Advance(ClipboardStreamReceiver.Watchdog * 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_clipboard.SetClipboardCallCount, Is.Zero);
            Assert.That(_clipboard.Text, Is.Null);
            Assert.That(_osd.Shown.Count, Is.Zero, "a sender's abort is a supersede, not a failure");
            Assert.That(slave.Sent.Of(MessageKind.ClipboardStreamAbort), Is.Empty);
        }
    }

    [Test]
    public async Task ChunksAfterTheReceiverAborts_ApplyNothing_AndTheUsersCopyStays()
    {
        var slave = Slave();
        var stream = StreamedPush(5, new ClipboardPushMessage(BigText('a')));
        await Begin(slave, stream);
        await Chunk(slave, stream.Chunks[0]);

        _clipboard.CanUseEchoFallbackValue = false;
        _clipboard.SetupText("the user's copy");
        await Chunks(slave, stream, from: 1);
        _clock.Advance(ClipboardStreamReceiver.Watchdog * 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(AbortsTo(slave.Sent, Master), Is.EqualTo([5L]),
                "one abort, then the rest are strangers");
            Assert.That(_clipboard.SetClipboardCallCount, Is.Zero);
            Assert.That(_clipboard.Text, Is.EqualTo("the user's copy"));
            Assert.That(_osd.Shown.Count, Is.Zero);
        }
    }

    /// <summary>
    /// A chunk from a host with no stream, whatever its id (even the id and offset of another host's stream), is ignored:
    /// no exception, no clear, no abort back, and the real stream beside it lands.
    /// </summary>
    [Test]
    public async Task AChunkFromAnUnknownHost_IsIgnored_AndTheRealStreamLands()
    {
        var slave = Slave();
        var text = BigText('a');
        var stream = StreamedPush(9, new ClipboardPushMessage(text));

        await Chunk(slave, new ClipboardStreamChunkMessage(Random.Shared.NextInt64(), 0, [1, 2, 3]), "stranger");
        var clearsBeforeBegin = _clipboard.ClearClipboardCallCount;
        await Begin(slave, stream);
        await Chunk(slave, stream.Chunks[0]);
        await Chunk(slave, stream.Chunks[1], "stranger");
        await Chunk(slave, new ClipboardStreamChunkMessage(Random.Shared.NextInt64(), stream.Chunks[1].Offset, [4, 5]), "stranger");
        await Chunks(slave, stream, from: 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(clearsBeforeBegin, Is.Zero, "a stranger's chunk empties nothing");
            Assert.That(_clipboard.ClearClipboardCallCount, Is.EqualTo(1));
            Assert.That(_clipboard.Text, Is.EqualTo(text));
            Assert.That(_osd.Shown.Count, Is.Zero);
            Assert.That(slave.Sent.Of(MessageKind.ClipboardStreamAbort), Is.Empty);
        }
    }

    // -- 6: a whole message racing a stream --

    /// <summary>
    /// Over the real relay: a whole push sent after a stream to the same host began supersedes it; whatever the old
    /// stream's chunk in flight does on the other lane, the receiver ends with the whole one.
    /// </summary>
    [Test]
    public async Task AWholePushAfterAStreamBegan_OverTheRelay_TheReceiverEndsWithTheWholeOne()
    {
        await using var pair = await ConnectedPair(Master);
        var (master, receiver) = pair;
        var relay = new BulkPacedRelay(master, parkAt: 2);
        await using var stream = new ClipboardStreamSender(relay, () => Target, NullLogger.Instance);
        var slave = Slave();

        stream.Send(Target, MessageKind.ClipboardPush, new ClipboardPushMessage(BigText('a')));
        await relay.Parked.WaitAsync(Bound);
        stream.Send(Target, MessageKind.ClipboardPush, new ClipboardPushMessage("newer"));
        relay.Gate.Release();
        await relay.ParkedForwarded.WaitAsync(Bound);
        // the in-flight chunk's invocation has returned, so the relay delivers the sentinel after it
        master.Send([Target], TestMessages.Sentinel());

        foreach (var frame in await receiver.ReadUntilSentinel())
            await slave.SimulateReceive(frame.Source, frame.Kind, frame.Json);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_clipboard.Text, Is.EqualTo("newer"));
            Assert.That(_osd.Shown.Count, Is.Zero);
        }
    }

    /// <summary>
    /// The old stream's one chunk in flight (a middle one, or the one that would complete it) lands before the abort,
    /// between the abort and the whole push, or after the push: the receiver ends with the whole push every time.
    /// </summary>
    [Test]
    public async Task AWholePushRacingTheStreamsChunkInFlight_TheWholeOneWins([Values] bool lastChunk, [Values(0, 1, 2)] int position)
    {
        var slave = Slave();
        var stream = StreamedPush(1, new ClipboardPushMessage(BigText('a')));
        var racing = lastChunk ? stream.Chunks.Length - 1 : 1;
        List<Func<Task>> input =
        [
            () => Abort(slave, 1),
            () => slave.SimulateReceive(Master, MessageKind.ClipboardPush, new ClipboardPushMessage("whole").ToSaneJson()),
        ];
        input.Insert(position, () => Chunk(slave, stream.Chunks[racing]));

        await Begin(slave, stream);
        foreach (var chunk in stream.Chunks.Take(racing)) await Chunk(slave, chunk);
        foreach (var step in input) await step();
        _clock.Advance(ClipboardStreamReceiver.Watchdog * 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_clipboard.Text, Is.EqualTo("whole"));
            Assert.That(_osd.Shown.Count, Is.Zero);
        }
    }

    // -- 7: ids across a sender restart --

    /// <summary>
    /// Each sender starts its ids at random, so a restarted process's ids are nowhere near the old one's: neither its
    /// first id nor any within a million of it.
    /// </summary>
    [Test]
    public async Task RestartedSenders_StartTheirIdsFarApart()
    {
        List<long> firsts = [];
        for (var i = 0; i < 20; i++) firsts.Add(await FirstStreamId());

        var close = firsts.SelectMany((a, i) => firsts.Skip(i + 1).Select(b => (a, b))).Where(p => Distance(p.a, p.b) < 1_000_000).ToList();
        Assert.That(close, Is.Empty);
    }

    /// <summary>
    /// After a restarted sender's new Begin, a chunk of the old process's stream to the same host is dropped even where
    /// its offset is the one the new stream expects next, so it never joins the new stream.
    /// </summary>
    [Test]
    public async Task AStaleChunkFromBeforeARestart_IsDroppedEvenWhenItsOffsetLinesUp([Values] bool atTheStart)
    {
        var slave = Slave();
        var text = BigText('b');
        var old = await StreamedBySender(new ClipboardPushMessage(BigText('a')));
        var fresh = await StreamedBySender(new ClipboardPushMessage(text));
        Assert.That(fresh.Begin.Id, Is.Not.EqualTo(old.Begin.Id), "pre-condition: the restart took a new id");

        await Begin(slave, old);
        await Chunk(slave, old.Chunks[0]);
        await Begin(slave, fresh);
        if (atTheStart)
            await Chunk(slave, old.Chunks[0]);
        else
        {
            await Chunk(slave, fresh.Chunks[0]);
            await Chunk(slave, old.Chunks[1]);
        }
        await Chunks(slave, fresh, from: atTheStart ? 0 : 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_clipboard.Text, Is.EqualTo(text));
            Assert.That(_clipboard.SetClipboardCallCount, Is.EqualTo(1));
            Assert.That(_osd.Shown.Count, Is.Zero);
        }
    }

    // -- 8: chunks out of order --

    [TestCase(true)]
    [TestCase(false)]
    public async Task ChunksOutOfOrder_FailTheStreamWithTheOsd_NeverApplied(bool duplicate)
    {
        _clipboard.SetupText("old on the slave");
        var slave = Slave();
        var stream = StreamedPush(1, new ClipboardPushMessage(BigText('a')));
        Assert.That(stream.Chunks, Has.Length.GreaterThan(3), "pre-condition: enough chunks to go wrong in the middle");

        await Begin(slave, stream);
        await Chunk(slave, stream.Chunks[0]);
        await Chunk(slave, stream.Chunks[1]);
        // a duplicate of the last chunk, or one skipped
        await Chunk(slave, stream.Chunks[duplicate ? 1 : 3]);
        var failure = await _osd.Shown.Next("the failure");
        await Chunks(slave, stream, from: 2);
        _clock.Advance(ClipboardStreamReceiver.Watchdog * 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure, Is.EqualTo(ClipboardStreamReceiver.FailedOsd));
            Assert.That(_osd.Shown.Count, Is.Zero, "the failure is shown once");
            Assert.That(AbortsTo(slave.Sent, Master), Is.EqualTo([1L]),
                "the sender is told to stop, once");
            Assert.That(_clipboard.SetClipboardCallCount, Is.Zero);
            Assert.That(_clipboard.Text, Is.Null, "an honest empty, never the old content");
        }
    }

    // -- helpers --

    private TestableSlaveRelay Slave() => _slaves.New(_clipboard, _osd, _clock);

    private static string LargeText(int seed, int length)
    {
        var random = new Random(seed);
        return string.Create(length, random, (span, r) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = (char)('a' + r.Next(26));
        });
    }

    private static byte[] FileBytes(int seed)
    {
        var file = new byte[10 * FileChunkBytes];
        new Random(seed).NextBytes(file);
        return file;
    }

    private static bool IsInput(ReceivedMessage message) => message.Kind is MessageKind.KeyEvent or MessageKind.KeyEventBatch;

    // distance between two ids on the wrapping 64-bit ring the sender counts on
    private static ulong Distance(long a, long b)
    {
        var d = unchecked((ulong)(a - b));
        return Math.Min(d, unchecked(0UL - d));
    }

    // a file as FileTransferService sends one: Start, chunks, Done, one in flight at a time
    private static async Task SendFile(IRelaySender relay, string host, byte[] file)
    {
        await relay.SendReliableAsync([host], MessageSerializer.Encode(MessageKind.FileTransferStart, new FileTransferStartMessage(["f.bin"], file.Length)));
        var seq = 0;
        foreach (var data in file.Chunk(FileChunkBytes))
            await relay.SendReliableAsync([host], MessageSerializer.Encode(MessageKind.FileTransferChunk, new FileTransferChunkMessage(seq++, data)));
        await relay.SendReliableAsync([host], MessageSerializer.Encode(MessageKind.FileTransferDone,
            new FileTransferDoneMessage(file.Length, SHA256.HashData(file))));
    }

    private static async Task<Arrivals> Collect(HydraTestClient client, Func<Arrivals, bool> done)
    {
        var arrivals = new Arrivals();
        while (!done(arrivals)) arrivals.Add(await client.WaitForMessage("the rest of the bulk"));
        return arrivals;
    }

    private static void AssertFileIntact(Arrivals arrivals, byte[] file)
    {
        var data = arrivals.FileChunks.SelectMany(c => c.Data).ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(arrivals.FileKinds.FirstOrDefault(), Is.EqualTo(MessageKind.FileTransferStart), "Start leads the file");
            Assert.That(arrivals.FileKinds.LastOrDefault(), Is.EqualTo(MessageKind.FileTransferDone), "Done trails the file");
            Assert.That(arrivals.FileChunks.Select(c => c.Sequence), Is.EqualTo(Enumerable.Range(0, arrivals.FileChunks.Count)), "file chunks in order");
            Assert.That(data, Is.EqualTo(file), "the file arrived intact");
            Assert.That(arrivals.Done?.Sha256, Is.EqualTo(SHA256.HashData(data)));
        }
    }

    // in order and whole, and a real receiver reassembles it to the push
    private void AssertClipboardIntact(Arrivals arrivals, ClipboardPushMessage push)
    {
        var blob = MessageSerializer.Encode(MessageKind.ClipboardPush, push);
        Assert.That(arrivals.ClipboardKinds.FirstOrDefault(), Is.EqualTo(MessageKind.ClipboardStreamBegin), "Begin leads the clipboard");
        var begin = arrivals.ClipboardBegin!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(arrivals.ClipboardChunks.Select(c => c.Id), Is.All.EqualTo(begin.Id));
            Assert.That(arrivals.ClipboardChunks.Select(c => c.Offset),
                Is.EqualTo(Enumerable.Range(0, arrivals.ClipboardChunks.Count).Select(i => i * ClipboardStreamSender.ChunkBytes)), "clipboard chunks in order");
            Assert.That(arrivals.ClipboardChunks.SelectMany(c => c.Data).ToArray(), Is.EqualTo(blob), "the clipboard arrived intact");
        }

        using var receiver = new ClipboardStreamReceiver(new FakeClipboardSync(), (_, _) => BeginDecision.Cleared, _ => BeginDecision.Cleared,
            _clock, NullLogger.Instance);
        receiver.OnBegin("peer", begin);
        // every chunk in turn: LastOrDefault over a Select on a list would feed it only the last
        DecodedMessage? completed = null;
        foreach (var chunk in arrivals.ClipboardChunks) completed = receiver.OnChunk("peer", chunk);
        // the encoded bytes, kind byte included, since a record compares its byte[] fields by reference
        Assert.That(completed is null ? null : MessageSerializer.Encode(completed.Kind, completed.Deserialize<ClipboardPushMessage>()), Is.EqualTo(blob),
            "a receiver reassembles the push");
    }

    private static void AssertStreamInOrder(List<SentMessage> sends, byte[] blob, string host)
    {
        Assert.That(sends.FirstOrDefault()?.Kind, Is.EqualTo(MessageKind.ClipboardStreamBegin), $"Begin leads {host}'s stream");
        var begin = sends[0].Json.FromSaneJson<ClipboardStreamBeginMessage>()!;
        var chunks = sends.Skip(1).Select(s => s.Json.FromSaneJson<ClipboardStreamChunkMessage>()!).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chunks.Select(c => c.Id), Is.All.EqualTo(begin.Id), $"{host}'s chunks carry its id");
            Assert.That(chunks.Select(c => c.Offset), Is.EqualTo(Enumerable.Range(0, chunks.Count).Select(i => i * ClipboardStreamSender.ChunkBytes)),
                $"{host}'s chunks in order");
            Assert.That(chunks.SelectMany(c => c.Data).ToArray(), Is.EqualTo(blob), $"{host}'s clipboard intact");
        }
    }

    private static List<SentMessage> StreamSends(FakeRelay relay) =>
        [.. relay.Sent.Snapshot().Where(s => s.Kind is MessageKind.ClipboardStreamBegin or MessageKind.ClipboardStreamChunk)];

    // the first stream id a fresh sender (a fresh process) gives out; it parks on the gate at its Begin
    private static async Task<long> FirstStreamId()
    {
        var relay = new FakeRelay();
        relay.Reliable.Hold();
        await using var sender = new ClipboardStreamSender(relay, () => null, NullLogger.Instance);
        sender.Send(Target, MessageKind.ClipboardPush, new ClipboardPushMessage(new string('x', 600_000)));
        await relay.Sent.WaitFor(MessageKind.ClipboardStreamBegin);
        return relay.Sent.Of<ClipboardStreamBeginMessage>(MessageKind.ClipboardStreamBegin).Single().Id;
    }

    // a push as a fresh sender (a fresh process) streams it, start to end
    private static async Task<StreamedClipboard> StreamedBySender(ClipboardPushMessage push)
    {
        var relay = new FakeRelay();
        await using var sender = new ClipboardStreamSender(relay, () => null, NullLogger.Instance);
        sender.Send(Master, MessageKind.ClipboardPush, push);
        await relay.Sent.WaitFor(MessageKind.ClipboardStreamChunk, ChunkCount(MessageSerializer.Encode(MessageKind.ClipboardPush, push)));
        return new StreamedClipboard(relay.Sent.Of<ClipboardStreamBeginMessage>(MessageKind.ClipboardStreamBegin).Single(),
            [.. relay.Sent.Of<ClipboardStreamChunkMessage>(MessageKind.ClipboardStreamChunk)]);
    }

    // what one client received of a file transfer and a clipboard stream, in arrival order
    private sealed class Arrivals
    {
        public List<MessageKind> FileKinds { get; } = [];
        public List<FileTransferChunkMessage> FileChunks { get; } = [];
        public FileTransferDoneMessage? Done { get; private set; }
        public List<MessageKind> ClipboardKinds { get; } = [];
        public List<ClipboardStreamChunkMessage> ClipboardChunks { get; } = [];
        public ClipboardStreamBeginMessage? ClipboardBegin { get; private set; }
        // bulk-lane messages so far
        public int Bulk { get; private set; }

        public bool FileDone => Done != null;
        public bool ClipboardDone => ClipboardBegin != null && ClipboardChunks.Sum(c => c.Data.Length) >= ClipboardBegin.TotalBytes;

        public void Add(ReceivedMessage message)
        {
            if (MessageLane.Of(message.Kind) == RelayLane.Bulk) Bulk++;
            switch (message.Kind)
            {
                case MessageKind.FileTransferStart:
                    FileKinds.Add(message.Kind);
                    break;
                case MessageKind.FileTransferChunk:
                    FileKinds.Add(message.Kind);
                    FileChunks.Add(message.As<FileTransferChunkMessage>());
                    break;
                case MessageKind.FileTransferDone:
                    FileKinds.Add(message.Kind);
                    Done = message.As<FileTransferDoneMessage>();
                    break;
                case MessageKind.ClipboardStreamBegin:
                    ClipboardKinds.Add(message.Kind);
                    ClipboardBegin = message.As<ClipboardStreamBeginMessage>();
                    break;
                case MessageKind.ClipboardStreamChunk:
                    ClipboardKinds.Add(message.Kind);
                    ClipboardChunks.Add(message.As<ClipboardStreamChunkMessage>());
                    break;
            }
        }
    }

    /// <summary>
    /// The real client, with every bulk-lane send from index <paramref name="parkAt"/> on (file and clipboard alike) held
    /// on <see cref="Gate"/> before it is passed on. Each producer sends one at a time, so each keeps its order.
    /// </summary>
    private sealed class BulkPacedRelay(IRelaySender inner, int parkAt) : NullRelaySender
    {
        private readonly TaskCompletionSource _parked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _parkedForwarded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _bulk;

        public Gate Gate { get; } = new(held: true);
        // the first bulk send held
        public Task Parked => _parked.Task;
        // that send has been handed to the client and its send has finished
        public Task ParkedForwarded => _parkedForwarded.Task;

        public override bool IsConnected => inner.IsConnected;

        public override void Send(string[] targetHosts, byte[] payload) => inner.Send(targetHosts, payload);

        public override void SendKeyEvent(string[] targetHosts, KeyEventMessage message) => inner.SendKeyEvent(targetHosts, message);

        public override async ValueTask SendReliableAsync(string[] targetHosts, byte[] payload, CancellationToken cancel = default)
        {
            if (MessageLane.Of(payload) != RelayLane.Bulk)
            {
                await inner.SendReliableAsync(targetHosts, payload, cancel);
                return;
            }

            var index = Interlocked.Increment(ref _bulk) - 1;
            if (index < parkAt)
            {
                await inner.SendReliableAsync(targetHosts, payload, cancel);
                return;
            }

            if (index == parkAt) _parked.TrySetResult();
            await Gate.Passed(cancel);
            try { await inner.SendReliableAsync(targetHosts, payload, cancel); }
            finally
            {
                if (index == parkAt) _parkedForwarded.TrySetResult();
            }
        }
    }

    private sealed record Pick(string Priority, int Sent);
}
