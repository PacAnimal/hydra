using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using Cathedral.Config;
using Cathedral.Extensions;
using Hydra.Platform;
using Hydra.Relay;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Tests.Setup;
using static Tests.Setup.ClipboardStreaming;

namespace Tests.Relay.Edge;

// content and sizes: the stream threshold and chunk boundaries, the 16 MiB cap, every field and the selection
// beside a stream, and byte-exact text, from end to end
[TestFixture]
public class ClipboardEdgeContentTests : MasterClipboardFixture
{
    private const int ChunkBytes = ClipboardStreamSender.ChunkBytes;

    private static readonly long Cap = ClipboardUtils.MaxClipboardBytes;

    private readonly SlaveRelays _slaves = new();
    private readonly List<ClipboardStreamSender> _senders = [];
    private RecordingOsd _osd = null!;
    private FakeTimeProvider _clock = null!;

    [SetUp]
    public void SetUp()
    {
        _osd = new RecordingOsd();
        _clock = new FakeTimeProvider();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _slaves.StopAll();
        foreach (var sender in _senders) await sender.DisposeAsync();
        _senders.Clear();
    }

    // -- the threshold and the chunk boundaries --

    [Test]
    public async Task EncodedAtExactlyTheThreshold_IsSentWhole()
    {
        var relay = new FakeRelay();
        var push = PushOfEncodedLength(ClipboardStreamSender.Threshold);

        Sender(relay).Send("a", MessageKind.ClipboardPush, push);
        // one this size may stream, so the sender's loop encodes it
        await relay.Sent.WaitFor(MessageKind.ClipboardPush, 1, "a");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(relay.Sent.Of<ClipboardPushMessage>(MessageKind.ClipboardPush, "a"), Is.EqualTo([push]));
            Assert.That(relay.Sent.Of(MessageKind.ClipboardStreamBegin), Is.Empty);
            Assert.That(relay.Sent.Of(MessageKind.ClipboardStreamChunk), Is.Empty);
        }
    }

    // the threshold plus one, and one chunk's worth over it either side
    [TestCase(1, 3, 1)]
    [TestCase(ChunkBytes - 1, 3, ChunkBytes - 1)]
    [TestCase(ChunkBytes, 3, ChunkBytes)]
    [TestCase(ChunkBytes + 1, 4, 1)]
    public async Task EncodedOverTheThreshold_StreamsInExactChunks_AndReassemblesExactly(int over, int chunkCount, int lastChunk)
    {
        var relay = new FakeRelay();
        var push = PushOfEncodedLength(ClipboardStreamSender.Threshold + over);
        var blob = MessageSerializer.Encode(MessageKind.ClipboardPush, push);

        Sender(relay).Send("a", MessageKind.ClipboardPush, push);
        var (beginSent, chunksSent) = await SentStream(relay.Sent, "a");

        var begin = As<ClipboardStreamBeginMessage>(beginSent);
        var chunks = chunksSent.Select(As<ClipboardStreamChunkMessage>).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(relay.Sent.Of(MessageKind.ClipboardPush), Is.Empty, "never sent whole");
            Assert.That(begin.TotalBytes, Is.EqualTo(blob.Length));
            Assert.That(begin.BlobHash, Is.EqualTo(XxHash64.HashToUInt64(blob)));
            Assert.That(relay.Sent.Of(MessageKind.ClipboardStreamChunk, "a"), Has.Count.EqualTo(chunkCount));
            Assert.That(chunks.Select(c => c.Offset), Is.EqualTo(Enumerable.Range(0, chunkCount).Select(i => i * ChunkBytes)));
            Assert.That(chunks.SkipLast(1).Select(c => c.Data.Length), Is.All.EqualTo(ChunkBytes));
            Assert.That(chunks[^1].Data, Has.Length.EqualTo(lastChunk));
            Assert.That(chunks.SelectMany(c => c.Data).ToArray(), Is.EqualTo(blob));
        }

        using var receiver = new ClipboardStreamReceiver(new FakeClipboardSync(), (_, _) => BeginDecision.Cleared, _ => BeginDecision.Cleared,
            _clock, NullLogger.Instance);
        receiver.OnBegin("a", begin);
        var completed = chunks.Select(c => receiver.OnChunk("a", c)).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(completed.SkipLast(1), Is.All.Null, "nothing completes before the last chunk");
            Assert.That(completed[^1]?.Kind, Is.EqualTo(MessageKind.ClipboardPush));
            Assert.That(completed[^1]?.Deserialize<ClipboardPushMessage>(), Is.EqualTo(push));
        }
    }

    // -- the 16 MiB cap --

    [Test]
    public async Task AClipboardAtTheCap_StreamsAndLands()
    {
        var master = new FakeClipboardSync();
        var text = new string('c', (int)Cap);
        master.SetupText(text);
        var slave = new FakeClipboardSync();
        slave.SetupText("old on the slave");

        await PushedToASlave(master, slave);

        Assert.That(slave.Text, Is.EqualTo(text));
    }

    [Test]
    public void OverTheCap_TrimToFit_DropsTheImageFirst()
    {
        var text = new string('t', 1024 * 1024);
        var html = new string('h', 1024);
        var rtf = new byte[1024];

        var trimmed = ClipboardUtils.TrimToFit(text, null, new byte[Cap], html, rtf, NullLogger.Instance, "test");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(trimmed.ImagePng, Is.Null);
            Assert.That(trimmed.Text, Is.EqualTo(text));
            Assert.That(trimmed.Html, Is.EqualTo(html));
            Assert.That(trimmed.Rtf, Is.EqualTo(rtf));
            Assert.That(Bytes(trimmed), Is.LessThanOrEqualTo(Cap));
        }
    }

    [Test]
    public void OverTheCap_TrimToFit_DropsTheRichTextBeforeThePlainText()
    {
        var text = new string('t', (int)Cap - 10);
        var html = new string('h', 100);
        var rtf = new byte[5];

        var trimmed = ClipboardUtils.TrimToFit(text, null, null, html, rtf, NullLogger.Instance, "test");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(trimmed.Html, Is.Null, "html goes first");
            Assert.That(trimmed.Rtf, Is.EqualTo(rtf), "rtf kept, since dropping html made it fit");
            Assert.That(trimmed.Text, Is.EqualTo(text));
            Assert.That(Bytes(trimmed), Is.LessThanOrEqualTo(Cap));
        }
    }

    [Test]
    public void OverTheCap_TrimToFit_DropsThePlainTextLast()
    {
        var trimmed = ClipboardUtils.TrimToFit(new string('t', (int)Cap + 1), null, null, null, null, NullLogger.Instance, "test");

        Assert.That(trimmed.IsEmpty, Is.True);
    }

    [Test]
    public async Task OneOverTheCap_IsTrimmedBeforeSending_TheSlaveNeverSeesAnOverCapField()
    {
        var master = new FakeClipboardSync();
        var text = new string('t', (int)Cap - 5);
        var rtf = new byte[] { 1, 2, 3, 4, 5 };
        // one byte over the cap, which the html alone takes it to
        master.SetClipboard(new ClipboardSnapshot(text, null, null, "h", rtf));
        var slave = new FakeClipboardSync();
        slave.SetupText("old on the slave");

        var (_, begin) = await PushedToASlave(master, slave);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(slave.Text, Is.EqualTo(text));
            Assert.That(slave.Html, Is.Null, "dropped on the master, before sending");
            Assert.That(slave.Rtf, Is.EqualTo(rtf));
            Assert.That(Bytes(new ClipboardSnapshot(slave.Text, null, slave.ImagePng, slave.Html, slave.Rtf)), Is.LessThanOrEqualTo(Cap));
            Assert.That(begin.ContentHash, Is.EqualTo(ClipboardUtils.ClipboardHash(new ClipboardSnapshot(text, null, null, null, rtf))));
        }
    }

    [Test]
    public async Task AnImageOneOverTheCap_IsTrimmedAway_NothingOverTheCapIsOffered()
    {
        var master = new FakeClipboardSync();
        master.SetupImage(new byte[Cap + 1]);
        var (platform, relay, service) = await StartMaster(master);
        TransitionTestHelper.EnterRemote(platform);

        await SimulatePullRequest(relay);
        await service.HashReads();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(relay.Sent.Of(MessageKind.ClipboardHash, "remote"), Is.Empty);
            Assert.That(relay.Sent.Of(MessageKind.ClipboardPush, "remote"), Is.Empty);
            Assert.That(relay.Sent.Of(MessageKind.ClipboardStreamBegin, "remote"), Is.Empty);
        }
    }

    [Test]
    public async Task ABeginAtTheReceiversCap_IsTaken()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the slave");
        var slave = Slave(clipboard);

        await slave.SimulateReceive(Master, MessageKind.ClipboardStreamBegin, ForgedBegin(1, (int)(Cap * 2)).ToSaneJson());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(clipboard.ClearClipboardCallCount, Is.EqualTo(1));
            Assert.That(clipboard.Text, Is.Null);
            Assert.That(_osd.Shown.Count, Is.Zero);
        }
    }

    [Test]
    public async Task AForgedBeginOverTheReceiversCap_IsRefusedWithTheFailure_AndNothingOfItApplies()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the slave");
        var slave = Slave(clipboard);
        var begin = ForgedBegin(9, (int)(Cap * 2) + 1);

        await slave.SimulateReceive(Master, MessageKind.ClipboardStreamBegin, begin.ToSaneJson());
        var failure = await _osd.Shown.Next("the failure");
        await slave.SimulateReceive(Master, MessageKind.ClipboardStreamChunk, new ClipboardStreamChunkMessage(9, 0, [1, 2, 3]).ToSaneJson());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure, Is.EqualTo(ClipboardStreamReceiver.FailedOsd));
            Assert.That(clipboard.SetClipboardCallCount, Is.Zero);
            // the user's copy is newer than whatever a forged Begin says is coming, so it stays
            Assert.That(clipboard.Text, Is.EqualTo("old on the slave"));
            Assert.That(clipboard.ClearClipboardCallCount, Is.Zero);
        }
    }

    // -- every field over the threshold --

    [Test]
    public async Task TextHtmlAndRtfOverTheThreshold_AllLandIntact_UnderTheHandshakesHash()
    {
        var master = new FakeClipboardSync();
        var snapshot = new ClipboardSnapshot(JustOverText('t'), null, null, "<p>" + JustOverText('h') + "</p>",
            Encoding.ASCII.GetBytes(@"{\rtf1 " + JustOverText('r') + "}"));
        master.SetClipboard(snapshot);
        var slave = new FakeClipboardSync();
        slave.SetupText("old on the slave");

        var (offered, begin) = await PushedToASlave(master, slave);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(slave.Text, Is.EqualTo(snapshot.Text));
            Assert.That(slave.Html, Is.EqualTo(snapshot.Html));
            Assert.That(slave.Rtf, Is.EqualTo(snapshot.Rtf));
            Assert.That(slave.ImagePng, Is.Null);
            Assert.That(begin.ContentHash, Is.EqualTo(offered.Hash));
            Assert.That(begin.ContentHash, Is.EqualTo(ClipboardWire.ToHash(snapshot).Hash));
        }
    }

    [Test]
    public async Task AnImageOnlyClipboardOverTheThreshold_LandsAsAnImage_WithNoText()
    {
        var master = new FakeClipboardSync();
        var image = JustOverImage(1);
        master.SetupImage(image);
        var slave = new FakeClipboardSync();
        slave.SetupText("old on the slave");

        var (offered, begin) = await PushedToASlave(master, slave);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(slave.ImagePng, Is.EqualTo(image));
            Assert.That(slave.Text, Is.Null);
            Assert.That(slave.Html, Is.Null);
            Assert.That(slave.Rtf, Is.Null);
            Assert.That(begin.ContentHash, Is.EqualTo(offered.Hash));
        }
    }

    [Test]
    public async Task AnImageOnlyAnswerOverTheThreshold_LandsOnTheMasterAsAnImage_WithNoText()
    {
        var master = new FakeClipboardSync();
        master.SetupText("old on the master");
        var slave = new FakeClipboardSync();
        var image = JustOverImage(2);
        slave.SetupImage(image);

        await ReturnedToTheMaster(master, slave);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(master.ImagePng, Is.EqualTo(image));
            Assert.That(master.Text, Is.Null);
        }
    }

    // -- the selection beside a stream (X11 slave) --

    [TestCase(false)]
    [TestCase(true)]
    public async Task SelectionChangesMidStream_IsTaken_AndTheStreamsCompletionKeepsTheNewerOne(bool newerIsTheText)
    {
        var clipboard = new SimulatedClipboard(ClipboardOs.Linux);
        clipboard.Copy("old on the slave");
        clipboard.Select("old selection on the slave");
        var slave = Slave(clipboard);
        var text = JustOverText('s');
        var copied = new ClipboardSnapshot(text, "selected when copied", null);
        var push = ClipboardWire.ToPush(copied);
        var stream = StreamedPush(3, push, ClipboardWire.ToHash(copied).Hash!.Value);
        var newer = newerIsTheText ? text : "selected since, on the master";

        await slave.SimulateReceive(Master, MessageKind.ClipboardStreamBegin, stream.Begin.ToSaneJson());
        await slave.SimulateReceive(Master, MessageKind.ClipboardStreamChunk, stream.Chunks[0].ToSaneJson());
        // the master's clipboard unchanged, its selection moved on: it travels inline beside the same hash
        var moved = ClipboardWire.ToHash(copied with { PrimaryText = newer });
        Assert.That(moved.PrimaryHash, Is.Null, "pre-condition: the selection travels inline");
        await slave.SimulateReceive(Master, MessageKind.ClipboardHash, moved.ToSaneJson());
        var midStream = clipboard.Primary;
        foreach (var chunk in stream.Chunks.Skip(1))
            await slave.SimulateReceive(Master, MessageKind.ClipboardStreamChunk, chunk.ToSaneJson());

        using (Assert.EnterMultipleScope())
        {
            // the text of a selection that repeats it is still on its way, so only a selection of its own is taken at once
            if (!newerIsTheText) Assert.That(midStream, Is.EqualTo(newer), "taken at once");
            Assert.That(slave.Sent.Of(MessageKind.ClipboardPullRequest), Is.Empty, "the stream counts as applied");
            Assert.That(slave.Sent.Of(MessageKind.ClipboardStreamAbort), Is.Empty, "the stream undisturbed");
            Assert.That(_osd.Shown.Count, Is.Zero);
            Assert.That(clipboard.Paste(), Is.EqualTo(text));
            Assert.That(clipboard.Primary, Is.EqualTo(newer), "the older selection the stream carried never wins");
        }
    }

    [Test]
    public async Task PendingWithAnInlineSelection_EmptiesTheClipboard_AndTakesTheSelection([Values] ClipboardOs os)
    {
        var clipboard = new SimulatedClipboard(os);
        clipboard.Copy("old on the slave");
        if (os == ClipboardOs.Linux) clipboard.Select("old selection on the slave");
        var slave = Slave(clipboard);
        const string selection = "selected on the master";

        await slave.SimulateReceive(Master, MessageKind.ClipboardHash, new ClipboardHashMessage(null, selection, Pending: true).ToSaneJson());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(clipboard.Paste(), Is.Null);
            Assert.That(clipboard.Primary, Is.EqualTo(selection));
            Assert.That(slave.Sent.Of(MessageKind.ClipboardPullRequest), Is.Empty);
        }

        // the master's copy lands and its real hash follows, the selection still beside it
        var landed = new ClipboardSnapshot("landed on the master", selection, null);
        await slave.SimulateReceive(Master, MessageKind.ClipboardHash, ClipboardWire.ToHash(landed).ToSaneJson());
        Assert.That(slave.Sent.Of(MessageKind.ClipboardPullRequest), Has.Count.EqualTo(1));
        await slave.SimulateReceive(Master, MessageKind.ClipboardPush, ClipboardWire.ToPush(landed).ToSaneJson());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(clipboard.Paste(), Is.EqualTo("landed on the master"));
            Assert.That(clipboard.Primary, Is.EqualTo(selection));
        }
    }

    // -- across every OS pairing --

    [Test]
    public void ALargeTextClipboardWithItsSelection_RoundTripsIntact_PastingNothingMidway([Values] ClipboardOs masterOs,
        [Values] ClipboardOs slaveOs, [Values] bool fromMaster)
    {
        var master = new SimulatedClipboard(masterOs);
        var slave = new SimulatedClipboard(slaveOs);
        var text = JustOverText('m');
        const string selection = "a selection beside it";
        var originOs = fromMaster ? masterOs : slaveOs;
        var expectedPrimary = originOs == ClipboardOs.Linux ? selection : text;

        var (origin, other, midway, writes) = RoundTrip(master, slave, fromMaster, c =>
        {
            c.Copy(text);
            if (originOs == ClipboardOs.Linux) c.Select(selection);
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(midway.Paste, Is.Null, "empty midway");
            Assert.That(midway.Image, Is.Null, "empty midway");
            Assert.That(other.Paste(), Is.EqualTo(text));
            Assert.That(other.Primary, Is.EqualTo(expectedPrimary));
            Assert.That(origin.Paste(), Is.EqualTo(text));
            Assert.That(origin.Primary, Is.EqualTo(expectedPrimary));
            Assert.That(origin.ClipboardWrites, Is.EqualTo(writes), "the way back rewrites nothing");
        }
    }

    [Test]
    public void ALargeImageOnlyClipboard_RoundTripsIntact_PastingNothingMidway([Values] ClipboardOs masterOs, [Values] ClipboardOs slaveOs,
        [Values] bool fromMaster)
    {
        var master = new SimulatedClipboard(masterOs);
        var slave = new SimulatedClipboard(slaveOs);
        var image = JustOverImage(3);

        var (origin, other, midway, writes) = RoundTrip(master, slave, fromMaster, c => c.CopyImage(image));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(midway.Paste, Is.Null, "empty midway");
            Assert.That(midway.Image, Is.Null, "empty midway");
            Assert.That(other.ImagePng, Is.EqualTo(image));
            Assert.That(other.Paste(), Is.Null, "no text beside the image");
            Assert.That(origin.ImagePng, Is.EqualTo(image));
            Assert.That(origin.ClipboardWrites, Is.EqualTo(writes), "the way back rewrites nothing");
        }
    }

    // -- text byte for byte --

    [TestCase("unicode")]
    [TestCase("crlf")]
    public async Task TextPushedOverTheThreshold_LandsByteExact(string kind)
    {
        var text = Awkward(kind);
        var master = new FakeClipboardSync();
        master.SetupText(text);
        var slave = new FakeClipboardSync();
        slave.SetupText("old on the slave");

        await PushedToASlave(master, slave);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(slave.Text, Is.EqualTo(text));
            Assert.That(Encoding.UTF8.GetBytes(slave.Text ?? ""), Is.EqualTo(Encoding.UTF8.GetBytes(text)));
        }
    }

    [TestCase("unicode")]
    [TestCase("crlf")]
    public async Task TextReturnedOverTheThreshold_LandsByteExact(string kind)
    {
        var text = Awkward(kind);
        var master = new FakeClipboardSync();
        master.SetupText("old on the master");
        var slave = new FakeClipboardSync();
        slave.SetupText(text);

        await ReturnedToTheMaster(master, slave);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(master.Text, Is.EqualTo(text));
            Assert.That(Encoding.UTF8.GetBytes(master.Text ?? ""), Is.EqualTo(Encoding.UTF8.GetBytes(text)));
        }
    }

    // -- helpers --

    private ClipboardStreamSender Sender(FakeRelay relay)
    {
        var sender = new ClipboardStreamSender(relay, () => null, NullLogger.Instance);
        _senders.Add(sender);
        return sender;
    }

    private TestableSlaveRelay Slave(IClipboardSync clipboard) => _slaves.New(clipboard, _osd, _clock);

    // the master's clipboard, entering "remote" and pushed to a slave on the far side; the hash the entry offered, and the
    // stream's Begin
    private async Task<PushedStream> PushedToASlave(FakeClipboardSync master, IClipboardSync slaveClipboard)
    {
        var (platform, relay, _) = await StartMaster(master);
        TransitionTestHelper.EnterRemote(platform);
        await relay.Sent.WaitFor(MessageKind.ClipboardHash, 1, "remote");
        var offered = relay.Sent.Of<ClipboardHashMessage>(MessageKind.ClipboardHash, "remote").Single();
        await SimulatePullRequest(relay);
        var (begin, chunks) = await SentStream(relay.Sent, "remote");

        var slave = Slave(slaveClipboard);
        await slave.SimulateReceive(Master, begin.Kind, begin.Json);
        // the entry hash again mid-stream: the pending stream counts as applied, so it asks for nothing
        await slave.SimulateReceive(Master, MessageKind.ClipboardHash, offered.ToSaneJson());
        foreach (var chunk in chunks) await slave.SimulateReceive(Master, chunk.Kind, chunk.Json);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(relay.Sent.Of(MessageKind.ClipboardPush), Is.Empty, "streamed, never whole");
            Assert.That(slave.Sent.Of(MessageKind.ClipboardPullRequest), Is.Empty, "the stream's hash is the one the handshake offered");
            Assert.That(slave.Sent.Of(MessageKind.ClipboardStreamAbort), Is.Empty);
            Assert.That(_osd.Shown.Count, Is.Zero);
        }
        return new PushedStream(offered, As<ClipboardStreamBeginMessage>(begin));
    }

    // the slave's clipboard, answering the master's pull as the cursor leaves "remote", streamed into the master
    private async Task ReturnedToTheMaster(FakeClipboardSync master, IClipboardSync slaveClipboard)
    {
        var (platform, relay, _) = await StartMaster(master);
        VisitAndLeaveRemote(platform);
        await relay.Sent.WaitFor(MessageKind.ClipboardPull, 1, "remote");
        var pull = relay.Sent.Of(MessageKind.ClipboardPull, "remote").Last();

        var slave = Slave(slaveClipboard);
        await slave.SimulateReceive(Master, pull.Kind, pull.Json);
        var (begin, chunks) = await SentStream(slave.Sent, Master);
        await relay.FireMessageReceived("remote", begin.Kind, begin.Json);
        var midway = new { master.Text, master.ImagePng };
        foreach (var chunk in chunks) await relay.FireMessageReceived("remote", chunk.Kind, chunk.Json);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(slave.Sent.Of(MessageKind.ClipboardPullResponse), Is.Empty, "streamed, never whole");
            Assert.That(midway.Text, Is.Null, "empty midway");
            Assert.That(midway.ImagePng, Is.Null, "empty midway");
        }
    }

    // copy on the origin, carried to the other side as a stream with a paste midway, then back, which finds it unchanged;
    // the origin's writes are counted from before the way back
    private static RoundTripped RoundTrip(SimulatedClipboard master, SimulatedClipboard slave, bool fromMaster, Action<SimulatedClipboard> copy)
    {
        var midway = new Midway("not run", []);
        if (fromMaster)
        {
            slave.Copy("old on the slave");
            copy(master);
            master.CarryTo(slave, midway: () => midway = new Midway(slave.Paste(), slave.ImagePng));
            var writes = master.ClipboardWrites;
            var answer = slave.ReturnTo(master);
            Assert.That(answer.Unchanged, Is.True, "the way back finds it unchanged");
            return new RoundTripped(master, slave, midway, writes);
        }

        master.Copy("old on the master");
        master.CarryTo(slave);
        copy(slave);
        slave.ReturnTo(master, midway: () => midway = new Midway(master.Paste(), master.ImagePng));
        var before = slave.ClipboardWrites;
        master.CarryTo(slave);
        return new RoundTripped(slave, master, midway, before);
    }

    // the latest stream sent to host, its Begin and every chunk, once the last has gone
    private static async Task<SentStreamed> SentStream(SentSignals sent, string host)
    {
        await sent.WaitFor(MessageKind.ClipboardStreamBegin, 1, host);
        var begin = sent.Of(MessageKind.ClipboardStreamBegin, host).Last();
        var id = As<ClipboardStreamBeginMessage>(begin).Id;
        var count = (As<ClipboardStreamBeginMessage>(begin).TotalBytes + ChunkBytes - 1) / ChunkBytes;
        var others = sent.Of<ClipboardStreamChunkMessage>(MessageKind.ClipboardStreamChunk, host).Count(c => c.Id != id);
        await sent.WaitFor(MessageKind.ClipboardStreamChunk, others + count, host);
        return new SentStreamed(begin, [.. sent.Of(MessageKind.ClipboardStreamChunk, host).Where(c => As<ClipboardStreamChunkMessage>(c).Id == id)]);
    }

    private static T As<T>(SentMessage message) => JsonSerializer.Deserialize<T>(message.Json, SaneJson.Options)!;

    // a push whose encoding is exactly length bytes: ascii padding adds one byte a character
    private static ClipboardPushMessage PushOfEncodedLength(int length)
    {
        var empty = MessageSerializer.Encode(MessageKind.ClipboardPush, new ClipboardPushMessage("")).Length;
        var push = new ClipboardPushMessage(new string('p', length - empty));
        Assert.That(MessageSerializer.Encode(MessageKind.ClipboardPush, push), Has.Length.EqualTo(length), "pre-condition: the encoded length");
        return push;
    }

    // a Begin no sender made, of totalBytes
    private static ClipboardStreamBeginMessage ForgedBegin(long id, int totalBytes) =>
        new(id, MessageKind.ClipboardPush, totalBytes, 0xBAD, 0xBAD);

    // the bytes TrimToFit counts against the cap
    private static long Bytes(ClipboardSnapshot s) =>
        (s.Text == null ? 0 : Encoding.UTF8.GetByteCount(s.Text)) + (s.ImagePng?.Length ?? 0) + (s.Html == null ? 0 : Encoding.UTF8.GetByteCount(s.Html))
        + (s.Rtf?.Length ?? 0) + (s.PrimaryText == null || s.SelectionIsText ? 0 : Encoding.UTF8.GetByteCount(s.PrimaryText));

    // a chunk over the stream threshold, so it streams in several chunks
    private static string JustOverText(char fill) => new(fill, ClipboardStreamSender.Threshold + ChunkBytes);

    private static byte[] JustOverImage(int seed)
    {
        var image = new byte[ClipboardStreamSender.Threshold + ChunkBytes];
        new Random(seed).NextBytes(image);
        return image;
    }

    // text whose bytes a careless path would change: non-ascii of every width, emoji sequences and combining marks, or
    // every line ending; over the threshold once encoded
    private static string Awkward(string kind)
    {
        var unit = kind == "unicode"
            ? "héllo wörld — 日本語 한국어 Ελληνικά 👩‍👩‍👧‍👦 🏳️‍🌈 👍🏽 e\u0301 \u200B\u200D\uFEFF 𝄞 \"quoted\" <tag> & \\ \t|"
            : "line one\r\nline two\r\n\r\n\ttabbed\rlone cr\nlone lf\r\n";
        // json escapes each character alone, so the units' encoded lengths add up
        var empty = MessageSerializer.Encode(MessageKind.ClipboardPush, new ClipboardPushMessage("")).Length;
        var perUnit = MessageSerializer.Encode(MessageKind.ClipboardPush, new ClipboardPushMessage(unit)).Length - empty;
        var text = string.Concat(Enumerable.Repeat(unit, (ClipboardStreamSender.Threshold + ChunkBytes) / perUnit + 1));
        Assert.That(MessageSerializer.Encode(MessageKind.ClipboardPush, new ClipboardPushMessage(text)),
            Has.Length.GreaterThan(ClipboardStreamSender.Threshold), "pre-condition: streamed");
        return text;
    }

    // the hash the entry offered, and the stream's Begin
    private sealed record PushedStream(ClipboardHashMessage Offered, ClipboardStreamBeginMessage Begin);

    // a stream as sent: its Begin and every chunk
    private sealed record SentStreamed(SentMessage Begin, List<SentMessage> Chunks);

    // what a round trip left: the clipboard copied on, the other side, what pasted midway, and the origin's writes before the way back
    private sealed record RoundTripped(SimulatedClipboard Origin, SimulatedClipboard Other, Midway Midway, int Writes);

    private sealed record Midway(string? Paste, byte[]? Image);
}
