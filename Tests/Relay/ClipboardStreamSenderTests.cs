using System.IO.Hashing;
using Cathedral.Extensions;
using System.Threading.Channels;
using Hydra.Platform;
using Hydra.Relay;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;
using static Tests.Setup.ClipboardStreaming;

namespace Tests.Relay;

[TestFixture]
public class ClipboardStreamSenderTests
{
    private SteppedRelay _relay = null!;
    private CapturingLog<ClipboardStreamSender> _log = null!;
    private string? _priority;
    private ClipboardStreamSender _sender = null!;

    [SetUp]
    public void SetUp()
    {
        _relay = new SteppedRelay();
        _log = new CapturingLog<ClipboardStreamSender>();
        _priority = null;
        _sender = new ClipboardStreamSender(_relay, () => Volatile.Read(ref _priority), _log);
    }

    [TearDown]
    public async Task TearDown() => await _sender.DisposeAsync();

    [Test]
    public void UnderTheThreshold_GoesWhole()
    {
        var push = new ClipboardPushMessage("hello");

        _sender.Send("a", MessageKind.ClipboardPush, push);

        var sent = _relay.Sent.Snapshot();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sent.Select(s => s.Kind), Is.EqualTo([MessageKind.ClipboardPush]));
            Assert.That(sent[0].Targets, Is.EqualTo(["a"]));
            Assert.That(_relay.Sent.Of<ClipboardPushMessage>(MessageKind.ClipboardPush).Single(), Is.EqualTo(push));
        }
    }

    [TestCase(MessageKind.ClipboardPush)]
    [TestCase(MessageKind.ClipboardPullResponse)]
    public async Task OverTheThreshold_BeginThenEveryChunkInOrder(MessageKind kind)
    {
        IClipboardPayload message = kind == MessageKind.ClipboardPush ? Large('x') : new ClipboardPullResponseMessage(7, new string('y', 600_000));
        var blob = message is ClipboardPushMessage push ? Send("a", push) : SendPullResponse("a", (ClipboardPullResponseMessage)message);

        var sends = await _relay.StepThrough(1 + ChunkCount(blob));

        Assert.That(sends[0].Kind, Is.EqualTo(MessageKind.ClipboardStreamBegin));
        var begin = sends[0].As<ClipboardStreamBeginMessage>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(begin.Inner, Is.EqualTo(kind));
            Assert.That(begin.TotalBytes, Is.EqualTo(blob.Length));
            Assert.That(begin.BlobHash, Is.EqualTo(XxHash64.HashToUInt64(blob)));
            Assert.That(begin.ContentHash, Is.EqualTo(ClipboardUtils.ClipboardHash(ClipboardWire.From(message, NullLogger.Instance, "test"))));
        }

        var chunks = sends.Skip(1).Select(s => s.As<ClipboardStreamChunkMessage>()).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sends.Skip(1).Select(s => s.Kind), Is.All.EqualTo(MessageKind.ClipboardStreamChunk));
            Assert.That(chunks.Select(c => c.Id), Is.All.EqualTo(begin.Id));
            Assert.That(chunks.Select(c => c.Offset), Is.EqualTo(Enumerable.Range(0, chunks.Count).Select(i => i * ClipboardStreamSender.ChunkBytes)));
            Assert.That(chunks.SelectMany(c => c.Data).ToArray(), Is.EqualTo(blob));
            Assert.That(_relay.Sent.Of(kind), Is.Empty, "never sent whole");
        }
    }

    [Test]
    public async Task AStreamedPush_CarriesTheHashOfItsSnapshot()
    {
        var snapshot = new ClipboardSnapshot("caption", "selected", new byte[700_000], "<b>caption</b>");

        Send("a", ClipboardWire.ToPush(snapshot));

        var begin = (await _relay.Next()).As<ClipboardStreamBeginMessage>();
        Assert.That(begin.ContentHash, Is.EqualTo(ClipboardWire.ToHash(snapshot).Hash));
    }

    [Test]
    public async Task Retain_StopsStreamsToHostsNotPresent_WithoutAnAbort()
    {
        Send("a", Large('a'));
        var aBegin = await _relay.Next();
        var blob = Send("b", Large('b'));

        _sender.Retain(new HashSet<string>(["B"]));
        aBegin.Gate.Release();
        var sends = await _relay.StepThrough(1 + ChunkCount(blob));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sends.Select(s => s.Host), Is.All.EqualTo("b"));
            Assert.That(sends.Skip(1).SelectMany(s => s.As<ClipboardStreamChunkMessage>().Data).ToArray(), Is.EqualTo(blob));
        }
        await AssertNothingMoreFor("a");
        Assert.That(_relay.Sent.Of(MessageKind.ClipboardStreamAbort), Is.Empty);
    }

    [Test]
    public void TheLanes_BeginAndAbortInput_ChunkBulk()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(MessageLane.Of(MessageKind.ClipboardStreamBegin), Is.EqualTo(RelayLane.Input));
            Assert.That(MessageLane.Of(MessageKind.ClipboardStreamAbort), Is.EqualTo(RelayLane.Input));
            Assert.That(MessageLane.Of(MessageKind.ClipboardStreamChunk), Is.EqualTo(RelayLane.Bulk));
        }
    }

    [Test]
    public async Task NoChunk_UntilBeginHasLeft()
    {
        Send("a", Large('x'));

        var begin = await _relay.Next();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(begin.Kind, Is.EqualTo(MessageKind.ClipboardStreamBegin));
            Assert.That(_relay.Sent.Of(MessageKind.ClipboardStreamChunk), Is.Empty);
        }
        begin.Gate.Release();
        Assert.That((await _relay.Next()).Kind, Is.EqualTo(MessageKind.ClipboardStreamChunk));
    }

    [Test]
    public async Task OneChunkInFlight()
    {
        Send("a", Large('x'));
        (await _relay.Next()).Gate.Release();

        var first = await _relay.Next();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Kind, Is.EqualTo(MessageKind.ClipboardStreamChunk));
            Assert.That(_relay.Sent.Of(MessageKind.ClipboardStreamChunk), Has.Count.EqualTo(1));
        }
        first.Gate.Release();
        using (Assert.EnterMultipleScope())
        {
            Assert.That((await _relay.Next()).As<ClipboardStreamChunkMessage>().Offset, Is.EqualTo(ClipboardStreamSender.ChunkBytes));
            Assert.That(_relay.Sent.Of(MessageKind.ClipboardStreamChunk), Has.Count.EqualTo(2));
        }
    }

    [Test]
    public async Task TheCursorsHost_GoesFirst_AndFollowsTheCursor()
    {
        Volatile.Write(ref _priority, "B");
        Send("a", Large('a'));
        var aBegin = await _relay.Next();
        Send("b", Large('b'));
        aBegin.Gate.Release();

        Assert.That(await NextStep(), Is.EqualTo("b Begin"));
        Assert.That(await NextStep(), Is.EqualTo("b 0"));
        Volatile.Write(ref _priority, "a");
        Assert.That(await NextStep(), Is.EqualTo("a 0"));
        Assert.That(await NextStep(), Is.EqualTo("a 1"));
        Volatile.Write(ref _priority, "b");
        Assert.That(await NextStep(), Is.EqualTo("b 1"));
        Assert.That(await NextStep(), Is.EqualTo("b 2"));
        Assert.That(await NextStep(), Is.EqualTo("a 2"));
    }

    [Test]
    public async Task WithoutTheCursor_StreamsTakeTurns()
    {
        Send("a", Large('a'));
        var aBegin = await _relay.Next();
        Send("b", Large('b'));
        aBegin.Gate.Release();

        List<string> hosts = [];
        for (var i = 0; i < 6; i++) hosts.Add((await _relay.Release()).Host);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hosts.Count(h => h == "a"), Is.EqualTo(3));
            Assert.That(hosts.Count(h => h == "b"), Is.EqualTo(3));
            Assert.That(hosts.Zip(hosts.Skip(1)).Count(p => p.First == p.Second), Is.Zero, "alternates");
        }
    }

    [Test]
    public async Task ANewSmallClipboard_AbortsTheStream()
    {
        Send("a", Large('x'));
        var oldId = (await _relay.Next()).As<ClipboardStreamBeginMessage>().Id;
        _relay.Pending!.Gate.Release();
        var chunk = await _relay.Next();

        Send("a", new ClipboardPushMessage("small"));
        chunk.Gate.Release();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_relay.Sent.Of<ClipboardStreamAbortMessage>(MessageKind.ClipboardStreamAbort, "a"),
                Is.EqualTo([new ClipboardStreamAbortMessage(oldId)]));
            Assert.That(_relay.Sent.Snapshot().Skip(2).Select(s => s.Kind),
                Is.EqualTo([MessageKind.ClipboardStreamAbort, MessageKind.ClipboardPush]), "abort, then the push");
        }
        await AssertNothingMoreFor("a");
    }

    [Test]
    public async Task AnUnchangedPullResponse_LeavesTheStreamingAnswerGoing()
    {
        var blob = SendPullResponse("a", new ClipboardPullResponseMessage(1, new string('x', 600_000)));
        var begin = (await _relay.Next()).As<ClipboardStreamBeginMessage>();
        _relay.Pending!.Gate.Release();
        var chunk = await _relay.Next();

        SendPullResponse("a", new ClipboardPullResponseMessage(2, null, Unchanged: true));
        chunk.Gate.Release();
        for (var i = 1; i < ChunkCount(blob); i++) await _relay.Release();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(begin.PullId, Is.EqualTo(1), "the Begin names the pull it answers");
            Assert.That(_relay.Sent.Of(MessageKind.ClipboardStreamAbort, "a"), Is.Empty);
            Assert.That(_relay.Sent.Of<ClipboardPullResponseMessage>(MessageKind.ClipboardPullResponse, "a").Single().PullId, Is.EqualTo(2));
            Assert.That(_relay.Sent.Of(MessageKind.ClipboardStreamChunk, "a"), Has.Count.EqualTo(ChunkCount(blob)));
        }
    }

    [Test]
    public async Task AnUnchangedPullResponse_StillAbortsAStreamingPush()
    {
        Send("a", Large('x'));
        var oldId = (await _relay.Next()).As<ClipboardStreamBeginMessage>().Id;

        SendPullResponse("a", new ClipboardPullResponseMessage(2, null, Unchanged: true));
        _relay.Pending!.Gate.Release();

        Assert.That(_relay.Sent.Of<ClipboardStreamAbortMessage>(MessageKind.ClipboardStreamAbort, "a"), Is.EqualTo([new ClipboardStreamAbortMessage(oldId)]));
        await AssertNothingMoreFor("a");
    }

    [Test]
    public async Task ANewLargeClipboard_AbortsTheStream_AndStreamsItself()
    {
        Send("a", Large('x'));
        var oldId = (await _relay.Next()).As<ClipboardStreamBeginMessage>().Id;
        _relay.Pending!.Gate.Release();
        var chunk = await _relay.Next();

        var blob = Send("a", Large('y'));
        chunk.Gate.Release();
        var sends = await _relay.StepThrough(1 + ChunkCount(blob));

        Assert.That(_relay.Sent.Of<ClipboardStreamAbortMessage>(MessageKind.ClipboardStreamAbort, "a"), Is.EqualTo([new ClipboardStreamAbortMessage(oldId)]));
        var newId = sends[0].As<ClipboardStreamBeginMessage>().Id;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(newId, Is.Not.EqualTo(oldId));
            Assert.That(sends.Skip(1).Select(s => s.As<ClipboardStreamChunkMessage>().Id), Is.All.EqualTo(newId));
            Assert.That(sends.Skip(1).SelectMany(s => s.As<ClipboardStreamChunkMessage>().Data).ToArray(), Is.EqualTo(blob));
        }
        var all = _relay.Sent.Snapshot();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(all.FindIndex(s => s.Kind == MessageKind.ClipboardStreamAbort),
                Is.LessThan(all.FindLastIndex(s => s.Kind == MessageKind.ClipboardStreamBegin)), "abort before the new begin");
            Assert.That(_relay.Sent.Of<ClipboardStreamChunkMessage>(MessageKind.ClipboardStreamChunk).Count(c => c.Id == oldId), Is.EqualTo(1));
        }
    }

    [Test]
    public async Task TheReceiversAbort_StopsTheStream_WithoutAnAbortBack()
    {
        Send("a", Large('x'));
        var id = (await _relay.Next()).As<ClipboardStreamBeginMessage>().Id;
        _relay.Pending!.Gate.Release();
        var chunk = await _relay.Next();

        _sender.OnAbort("a", new ClipboardStreamAbortMessage(id));
        chunk.Gate.Release();

        await AssertNothingMoreFor("a");
        Assert.That(_relay.Sent.Of(MessageKind.ClipboardStreamAbort), Is.Empty);
    }

    [Test]
    public async Task AnAbortForAnotherId_IsIgnored()
    {
        Send("a", Large('x'));
        var id = (await _relay.Next()).As<ClipboardStreamBeginMessage>().Id;
        _sender.OnAbort("a", new ClipboardStreamAbortMessage(id - 1));
        _relay.Pending!.Gate.Release();

        Assert.That((await _relay.Next()).Kind, Is.EqualTo(MessageKind.ClipboardStreamChunk));
    }

    [Test]
    public async Task Retain_WithoutThatHost_StopsItsStream_WithoutAnAbort()
    {
        Send("a", Large('x'));
        (await _relay.Next()).Gate.Release();
        var chunk = await _relay.Next();

        _sender.Retain(new HashSet<string>());
        chunk.Gate.Release();

        await AssertNothingMoreFor("a");
        Assert.That(_relay.Sent.Of(MessageKind.ClipboardStreamAbort), Is.Empty);
    }

    [Test]
    public async Task DropAll_StopsEveryStream_WithoutAnAbort()
    {
        Send("a", Large('a'));
        var aBegin = await _relay.Next();
        Send("b", Large('b'));

        _sender.DropAll();
        aBegin.Gate.Release();

        await AssertNothingMoreFor("a", "b");
        Assert.That(_relay.Sent.Of(MessageKind.ClipboardStreamAbort), Is.Empty);
    }

    [Test]
    public async Task AbortPushes_AbortsEveryPushStreamOnce_AndLeavesPullResponsesStreaming()
    {
        Send("a", Large('a'));
        var aBegin = await _relay.Next();
        var aId = aBegin.As<ClipboardStreamBeginMessage>().Id;
        Send("b", Large('b'));
        var blob = SendPullResponse("c", new ClipboardPullResponseMessage(7, new string('c', 600_000)));

        _sender.AbortPushes();
        aBegin.Gate.Release();
        var sends = await _relay.StepThrough(1 + ChunkCount(blob));

        var aborts = _relay.Sent.Snapshot().Where(s => s.Kind == MessageKind.ClipboardStreamAbort).Select(s => s.Targets.Single()).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(aborts, Is.EquivalentTo(["a", "b"]));
            Assert.That(_relay.Sent.Of<ClipboardStreamAbortMessage>(MessageKind.ClipboardStreamAbort, "a"), Is.EqualTo([new ClipboardStreamAbortMessage(aId)]));
            Assert.That(sends.Select(s => s.Host), Is.All.EqualTo("c"));
            Assert.That(sends.Skip(1).SelectMany(s => s.As<ClipboardStreamChunkMessage>().Data).ToArray(), Is.EqualTo(blob));
        }
        await AssertNothingMoreFor("a", "b");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_relay.Sent.Of(MessageKind.ClipboardStreamChunk, "a"), Is.Empty);
            Assert.That(_relay.Sent.Of(MessageKind.ClipboardStreamBegin, "b"), Is.Empty);
            Assert.That(_relay.Sent.Of(MessageKind.ClipboardStreamAbort), Has.Count.EqualTo(2));
        }
    }

    [Test]
    public async Task AbortAll_AbortsEveryStreamAsFailed_AndAbortPushesAsASupersede()
    {
        Send("a", Large('a'));
        var aBegin = await _relay.Next();
        var aId = aBegin.As<ClipboardStreamBeginMessage>().Id;
        Send("b", Large('b'));
        _sender.AbortPushes();
        Send("b", Large('c'));
        var bId = aId + 2;

        _sender.AbortAll();
        aBegin.Gate.Release();
        await AssertNothingMoreFor("a", "b");

        var aborts = _relay.Sent.Of<ClipboardStreamAbortMessage>(MessageKind.ClipboardStreamAbort);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(aborts.Where(a => a.Failed == true).Select(a => a.Id), Is.EquivalentTo([bId]), "dormancy gives up on what still streams");
            Assert.That(aborts.Where(a => a.Failed != true).Select(a => a.Id), Is.EquivalentTo([aId, aId + 1]), "a changed clipboard supersedes");
        }
    }

    // C5: the encoding of a message that may stream is the loop's, never the caller's
    [Test]
    public async Task ALargeMessage_IsEncodedByTheLoop_NotTheCaller()
    {
        Send("a", Large('a'));
        var aBegin = await _relay.Next();
        var image = new byte[(int)(ClipboardStreamReceiver.MaxTotalBytes * 3L / 4)];

        // over the cap, so encoding it warns; the loop is held in a's Begin, so nothing of it is encoded yet
        _sender.Send("b", MessageKind.ClipboardPush, new ClipboardPushMessage("caption", ImagePng: image));
        var warnedInSend = _log.Entries.Any(e => e.Level == LogLevel.Warning);
        _sender.Retain(new HashSet<string>(["b"]));
        aBegin.Gate.Release();
        // the image trimmed, the rest goes whole
        await _relay.Sent.WaitFor(MessageKind.ClipboardPush, 1, "b");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(warnedInSend, Is.False, "not encoded on the caller's thread");
            Assert.That(_relay.Sent.Of<ClipboardPushMessage>(MessageKind.ClipboardPush, "b"), Is.EqualTo([new ClipboardPushMessage("caption")]));
            Assert.That(_log.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message), Has.Some.Contains("image"));
        }
    }

    // C5: a small message after a large one still waiting for the loop goes at once, after the large one's abort
    [Test]
    public async Task ASmallMessageAfterAQueuedLargeOne_GoesAtOnce_AfterItsAbort()
    {
        Send("a", Large('a'));
        var aBegin = await _relay.Next();
        Send("b", Large('b'));

        Send("b", new ClipboardPushMessage("small"));
        var atOnce = _relay.Sent.Snapshot().Where(s => s.Targets.SequenceEqual(["b"])).Select(s => s.Kind).ToList();
        _sender.Retain(new HashSet<string>(["b"]));
        aBegin.Gate.Release();
        await AssertNothingMoreFor("b");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(atOnce, Is.EqualTo([MessageKind.ClipboardStreamAbort, MessageKind.ClipboardPush]), "the abort, then the push");
            Assert.That(_relay.Sent.Of<ClipboardPushMessage>(MessageKind.ClipboardPush, "b"), Is.EqualTo([new ClipboardPushMessage("small")]));
            Assert.That(_relay.Sent.Of(MessageKind.ClipboardStreamBegin, "b"), Is.Empty);
        }
    }

    [Test]
    public async Task AFailedSend_DropsThatStreamOnly()
    {
        _relay.FailFor = "a";
        var blob = Send("b", Large('b'));
        var bBegin = await _relay.Next();
        Send("a", Large('a'));
        bBegin.Gate.Release();

        var sends = await _relay.StepThrough(ChunkCount(blob));

        Assert.That(sends.SelectMany(s => s.As<ClipboardStreamChunkMessage>().Data).ToArray(), Is.EqualTo(blob));
        await _log.WaitFor(e => e.Level == LogLevel.Warning);
        await _relay.Sent.WaitFor(MessageKind.ClipboardStreamAbort, 1, "a");
        var aId = _relay.Sent.Of<ClipboardStreamBeginMessage>(MessageKind.ClipboardStreamBegin, "a").Single().Id;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_relay.Sent.Of(MessageKind.ClipboardStreamChunk, "a"), Is.Empty);
            Assert.That(_relay.Sent.Of(MessageKind.ClipboardStreamBegin, "a"), Has.Count.EqualTo(1));
            Assert.That(_relay.Sent.Of<ClipboardStreamAbortMessage>(MessageKind.ClipboardStreamAbort, "a"),
                Is.EqualTo([new ClipboardStreamAbortMessage(aId, Failed: true)]), "the receiver is told it failed, rather than waiting out its watchdog");
            Assert.That(_relay.Sent.Of(MessageKind.ClipboardStreamAbort, "b"), Is.Empty);
        }
    }

    [Test]
    public async Task AStreamReplacedBeforeItsBeginLeft_ItsBeginNeverGoesOut()
    {
        Send("a", Large('x'));
        var parked = await _relay.Next();
        Send("a", Large('y'));
        Send("a", new ClipboardPushMessage("small"));

        parked.Gate.Release();
        await AssertNothingMoreFor("a");

        var begins = _relay.Sent.Of<ClipboardStreamBeginMessage>(MessageKind.ClipboardStreamBegin, "a");
        var aborts = _relay.Sent.Of<ClipboardStreamAbortMessage>(MessageKind.ClipboardStreamAbort, "a");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(begins, Has.Count.EqualTo(1), "only the first stream's Begin, sent before it was replaced");
            Assert.That(aborts, Has.Count.EqualTo(2));
            Assert.That(aborts[0].Id, Is.EqualTo(begins[0].Id));
            Assert.That(_relay.Sent.Of(MessageKind.ClipboardStreamChunk, "a"), Is.Empty);
            AssertNothingAfterItsAbort();
        }
    }

    // replaced as fast as the loop takes them: whatever the interleaving, nothing of a stream follows its Abort
    [Test]
    public async Task StreamsReplacedAsTheLoopRuns_NothingOfOneFollowsItsAbort()
    {
        await _sender.DisposeAsync();
        var relay = new FakeRelay();
        _sender = new ClipboardStreamSender(relay, () => null, _log);
        for (var i = 0; i < 100; i++) _sender.Send("a", MessageKind.ClipboardPush, Large((char)('a' + i % 26)));
        // a whole push, sent in Send: every stream before it is aborted by the time Send returns
        _sender.Send("a", MessageKind.ClipboardPush, new ClipboardPushMessage("small"));

        AssertNothingAfterItsAbort(relay.Sent);
    }

    [Test]
    public async Task AfterDispose_EveryCallIsANoOp()
    {
        Send("a", Large('a'));
        var begin = await _relay.Next();
        var id = begin.As<ClipboardStreamBeginMessage>().Id;
        var sent = _relay.Sent.Snapshot().Count;

        await _sender.DisposeAsync();

        Assert.DoesNotThrow(() =>
        {
            _sender.Send("a", MessageKind.ClipboardPush, Large('b'));
            _sender.Send("b", MessageKind.ClipboardPush, new ClipboardPushMessage("small"));
            _sender.AbortPushes();
            _sender.OnAbort("a", new ClipboardStreamAbortMessage(id));
            _sender.Retain(new HashSet<string>());
            _sender.DropAll();
        });
        Assert.That(_relay.Sent.Snapshot(), Has.Count.EqualTo(sent), "nothing more sent");
    }

    // a message that cannot be encoded never goes, and its owner is told, so a slave awaiting it is not left waiting
    [Test]
    public async Task AnEncodeFailure_TellsTheOwner()
    {
        var undelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sender.EncodeFault = () => throw new InvalidOperationException("encode failed");

        _sender.Send("a", MessageKind.ClipboardPush, Large('x'), undelivered.SetResult);

        await undelivered.Task.WaitAsync(TestBound.Bound);
        Assert.That(_relay.Sent.Snapshot(), Is.Empty);
    }

    [Test]
    public void ANonClipboardKind_Throws() =>
        Assert.Throws<ArgumentException>(() => _sender.Send("a", MessageKind.KeyEvent, new ClipboardPushMessage("x")));

    // base64 makes the image alone outgrow the receiver's cap: it goes first, and the rest still streams
    [Test]
    public async Task AnEncodingOverTheReceiversCap_DropsTheImageFirstAndStillStreams()
    {
        var image = new byte[(int)(ClipboardStreamReceiver.MaxTotalBytes * 3L / 4)];
        var push = new ClipboardPushMessage("caption", ImagePng: image, Html: new string('h', 600_000), Rtf: [1, 2, 3]);
        Assert.That(MessageSerializer.Encode(MessageKind.ClipboardPush, push), Has.Length.GreaterThan(ClipboardStreamReceiver.MaxTotalBytes));

        _sender.Send("a", MessageKind.ClipboardPush, push);
        var blob = MessageSerializer.Encode(MessageKind.ClipboardPush, push with { ImagePng = null });
        var sends = await _relay.StepThrough(1 + ChunkCount(blob));

        var received = MessageSerializer.Decode([.. sends.Skip(1).SelectMany(s => s.As<ClipboardStreamChunkMessage>().Data)])
            .Deserialize<ClipboardPushMessage>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sends[0].As<ClipboardStreamBeginMessage>().TotalBytes, Is.EqualTo(blob.Length));
            Assert.That(received.ImagePng, Is.Null);
            Assert.That(received.Html, Is.EqualTo(push.Html));
            Assert.That(received.Rtf, Is.EqualTo(push.Rtf));
            Assert.That(received.Text, Is.EqualTo("caption"));
            Assert.That(_log.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message), Has.Exactly(1).Matches(Does.Contain("image")));
        }
    }

    // TrimToFit's order: the selection goes first when that alone brings the encoding within the cap
    [Test]
    public void EncodeWithinCap_DroppingTheSelectionAloneFits_DropsOnlyIt()
    {
        var image = new byte[(int)(ClipboardStreamReceiver.MaxTotalBytes * 3L / 8)];
        var push = new ClipboardPushMessage("caption", new string('p', (int)(ClipboardStreamReceiver.MaxTotalBytes * 6L / 10)), image);
        Assert.That(MessageSerializer.Encode(MessageKind.ClipboardPush, push), Has.Length.GreaterThan(ClipboardStreamReceiver.MaxTotalBytes));

        var encoded = ClipboardEncoding.EncodeWithinCap(MessageKind.ClipboardPush, push, _log);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(encoded.Message, Is.EqualTo(push with { PrimaryText = null }));
            var warnings = _log.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message);
            Assert.That(warnings, Has.Exactly(1).Matches(Does.Contain("primary text")));
        }
    }

    // a selection whose dropping alone is not enough waits its turn, after the image and the rich formats
    [Test]
    public void EncodeWithinCap_DroppingTheSelectionAloneIsNotEnough_DropsTheImageFirst()
    {
        var image = new byte[(int)(ClipboardStreamReceiver.MaxTotalBytes * 3L / 4) + 1024];
        var push = new ClipboardPushMessage("caption", "selected", image);

        var encoded = ClipboardEncoding.EncodeWithinCap(MessageKind.ClipboardPush, push, _log);

        Assert.That(encoded.Message, Is.EqualTo(push with { ImagePng = null }));
    }

    [Test]
    public async Task AnEncodingWithinTheReceiversCap_StreamsUntouched()
    {
        var push = new ClipboardPushMessage("caption", "selected", new byte[700_000], "<b>caption</b>", [1, 2, 3]);
        var blob = Send("a", push);

        var sends = await _relay.StepThrough(1 + ChunkCount(blob));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sends.Skip(1).SelectMany(s => s.As<ClipboardStreamChunkMessage>().Data).ToArray(), Is.EqualTo(blob));
            Assert.That(_log.Entries.Where(e => e.Level == LogLevel.Warning), Is.Empty);
        }
    }

    // a stream's Begin, chunks and Abort, by position: nothing of it sent after its Abort
    private void AssertNothingAfterItsAbort(SentSignals? sent = null)
    {
        var all = (sent ?? _relay.Sent).Snapshot();
        for (var i = 0; i < all.Count; i++)
        {
            if (all[i].Kind != MessageKind.ClipboardStreamAbort) continue;
            var id = all[i].Json.FromSaneJson<ClipboardStreamAbortMessage>()!.Id;
            var later = all.Skip(i + 1).Where(s => s.Targets.SequenceEqual(all[i].Targets) && StreamIdOf(s) == id).ToList();
            Assert.That(later, Is.Empty, $"nothing of stream {id} after its Abort");
        }
    }

    private static long? StreamIdOf(SentMessage sent) => sent.Kind switch
    {
        MessageKind.ClipboardStreamBegin => sent.Json.FromSaneJson<ClipboardStreamBeginMessage>()!.Id,
        MessageKind.ClipboardStreamChunk => sent.Json.FromSaneJson<ClipboardStreamChunkMessage>()!.Id,
        _ => null,
    };

    // -- dispatch beside a stalled send --

    // a chunk stalled to one host holds up no other host's queued message: it is still encoded and handed over
    [Test]
    public async Task OneHostsSendStalled_AnotherHostsQueuedMessagesStillGo()
    {
        Send("a", Large('a'));
        var stalled = await _relay.Next();
        SendPullResponse("b", new ClipboardPullResponseMessage(1, new string('b', 600_000)));
        SendPullResponse("b", new ClipboardPullResponseMessage(2, null, Unchanged: true));

        await _relay.Sent.WaitFor(MessageKind.ClipboardPullResponse, 1, "b");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stalled.Kind, Is.EqualTo(MessageKind.ClipboardStreamBegin), "pre-condition: a's Begin is in flight");
            Assert.That(stalled.Gate.IsHeld, Is.True, "pre-condition: a's Begin is still held");
            Assert.That(_relay.Sent.Of<ClipboardPullResponseMessage>(MessageKind.ClipboardPullResponse, "b").Single().PullId, Is.EqualTo(2));
            Assert.That(_relay.Sent.Of(MessageKind.ClipboardStreamBegin, "b"), Is.Empty, "one chunk in flight: b's Begin waits");
        }
    }

    private byte[] Send(string host, ClipboardPushMessage message)
    {
        _sender.Send(host, MessageKind.ClipboardPush, message);
        return MessageSerializer.Encode(MessageKind.ClipboardPush, message);
    }

    private byte[] SendPullResponse(string host, ClipboardPullResponseMessage message)
    {
        _sender.Send(host, MessageKind.ClipboardPullResponse, message);
        return MessageSerializer.Encode(MessageKind.ClipboardPullResponse, message);
    }

    // over the threshold, three chunks
    private static ClipboardPushMessage Large(char fill) => new(new string(fill, 600_000));

    // releases the pending send and names the one after it: "host Begin" or "host chunkIndex"
    private async Task<string> NextStep()
    {
        var send = await _relay.Release();
        return send.Kind == MessageKind.ClipboardStreamBegin
            ? $"{send.Host} Begin"
            : $"{send.Host} {send.As<ClipboardStreamChunkMessage>().Offset / ClipboardStreamSender.ChunkBytes}";
    }

    // a sentinel stream runs start to end on its own, which a stream still queued would interleave with
    private async Task AssertNothingMoreFor(params string[] hosts)
    {
        var blob = Send("sentinel", Large('s'));
        var sends = await _relay.StepThrough(1 + ChunkCount(blob));
        Assert.That(sends.Select(s => s.Host), Is.All.EqualTo("sentinel"));
        foreach (var host in hosts)
            Assert.That(_relay.Sent.Of(MessageKind.ClipboardStreamChunk, host), Has.Count.LessThanOrEqualTo(1), $"nothing more to {host}");
    }

    // every reliable send waits for the test to release it, so the test takes the loop one send at a time
    private sealed class SteppedRelay : NullRelaySender
    {
        private readonly Channel<ReliableSend> _reliable = Channel.CreateUnbounded<ReliableSend>();

        public SentSignals Sent { get; } = new();
        public string? FailFor { get; set; }

        // the last send taken by Next, waiting for its release
        public ReliableSend? Pending { get; private set; }

        public override void Send(string[] targetHosts, byte[] payload) => Sent.Record(targetHosts, payload);

        public override async ValueTask SendReliableAsync(string[] targetHosts, byte[] payload, CancellationToken cancel = default)
        {
            Sent.Record(targetHosts, payload);
            if (FailFor is { } failing && targetHosts.Contains(failing)) throw new InvalidOperationException("relay refused");
            var send = new ReliableSend(targetHosts.Single(), payload, new Gate(held: true));
            _reliable.Writer.TryWrite(send);
            await send.Gate.Passed(cancel);
        }

        // the next reliable send, held
        public async Task<ReliableSend> Next() => Pending = await _reliable.Reader.ReadAsync().AsTask().WaitAsync(Bound);

        // releases the pending send, then takes the next
        public async Task<ReliableSend> Release()
        {
            Pending?.Gate.Release();
            return await Next();
        }

        // releases the pending send, then takes and releases the next count, one at a time
        public async Task<List<ReliableSend>> StepThrough(int count)
        {
            Pending?.Gate.Release();
            List<ReliableSend> taken = [];
            while (taken.Count < count)
            {
                var send = await Next();
                taken.Add(send);
                send.Gate.Release();
            }
            return taken;
        }
    }

    private sealed record ReliableSend(string Host, byte[] Payload, Gate Gate)
    {
        public MessageKind Kind => (MessageKind)Payload[0];
        public T As<T>() => MessageSerializer.Decode(Payload).Deserialize<T>();
    }
}
