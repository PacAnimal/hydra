using Cathedral.Extensions;
using Hydra.Relay;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Tests.Setup;
using static Tests.Setup.ClipboardStreaming;

namespace Tests.Relay.Edge;

// several masters on one slave (decision 3): the last to start sending a clipboard wins, the stream it superseded is
// ignored, though that master may start a newer one, and the slave's handshake state is kept per master
[TestFixture]
public class ClipboardEdgeMultiMasterTests
{
    private const string M1 = "master-one";
    private const string M2 = "master-two";

    private readonly SlaveRelays _slaves = new();
    private FakeClipboardSync _clipboard = null!;
    private RecordingOsd _osd = null!;
    private FakeTimeProvider _clock = null!;
    private CapturingLog<RelayConnection> _log = null!;

    [SetUp]
    public void SetUp()
    {
        _clipboard = new FakeClipboardSync();
        _osd = new RecordingOsd();
        _clock = new FakeTimeProvider();
        _log = new CapturingLog<RelayConnection>(LogLevel.Warning);
    }

    [TearDown]
    public Task TearDown() => _slaves.StopAll();

    [Test]
    public async Task SecondMastersBegin_AbortsTheFirstsStream_AndLands()
    {
        var slave = Slave();
        var first = StreamedPush(1, new ClipboardPushMessage(BigText('a')));
        var second = StreamedPush(1, new ClipboardPushMessage(BigText('b')));
        await Begin(slave, first, M1);
        await Chunk(slave, first.Chunks[0], M1);

        await Begin(slave, second, M2);
        await Chunks(slave, first, from: 1, host: M1);
        var setByFirst = _clipboard.SetClipboardCallCount;
        await Chunks(slave, second, host: M2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(AbortsTo(slave.Sent, M1), Is.EqualTo([1L]));
            Assert.That(AbortsTo(slave.Sent, M2), Is.Empty);
            Assert.That(setByFirst, Is.Zero);
            Assert.That(_clipboard.Text, Is.EqualTo(BigText('b')));
            Assert.That(_osd.Shown.Count, Is.Zero, "a superseded stream is no failure");
        }
    }

    [Test]
    public async Task SecondMastersWholePush_AbortsTheFirstsStream_AndLands()
    {
        var slave = Slave();
        var first = StreamedPush(3, new ClipboardPushMessage(BigText('a')));
        await Begin(slave, first, M1);
        await Chunk(slave, first.Chunks[0], M1);

        await Push(slave, M2, new ClipboardPushMessage("from the second"));
        await Chunks(slave, first, from: 1, host: M1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(AbortsTo(slave.Sent, M1), Is.EqualTo([3L]));
            Assert.That(_clipboard.Text, Is.EqualTo("from the second"));
            Assert.That(_clipboard.SetClipboardCallCount, Is.EqualTo(1));
            Assert.That(_osd.Shown.Count, Is.Zero, "a superseded stream is no failure");
        }
    }

    // only the superseded master's old stream is held off: its chunks apply and clear nothing
    [Test]
    public async Task WhileTheWinnerStreams_TheSupersededMastersOldStream_IsIgnored_AndTheWinnerLands()
    {
        var slave = Slave();
        var first = StreamedPush(1, new ClipboardPushMessage(BigText('a')));
        var second = StreamedPush(1, new ClipboardPushMessage(BigText('b')));
        await Begin(slave, first, M1);
        await Begin(slave, second, M2);
        await Chunk(slave, second.Chunks[0], M2);
        var clears = _clipboard.ClearClipboardCallCount;

        await Chunks(slave, first, host: M1);
        var whileStreaming = new { _clipboard.SetClipboardCallCount, _clipboard.ClearClipboardCallCount, M2Aborts = AbortsTo(slave.Sent, M2) };
        await Chunks(slave, second, from: 1, host: M2);
        var landed = _clipboard.Text;
        await Push(slave, M1, new ClipboardPushMessage("after the winner"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(whileStreaming.SetClipboardCallCount, Is.Zero);
            Assert.That(whileStreaming.ClearClipboardCallCount, Is.EqualTo(clears));
            Assert.That(whileStreaming.M2Aborts, Is.Empty);
            Assert.That(landed, Is.EqualTo(BigText('b')));
            Assert.That(_clipboard.Text, Is.EqualTo("after the winner"));
        }
    }

    // a copy made on the superseded master since is newer than the winner's: its hash asks for it, and its push
    // starts last, so it wins
    [Test]
    public async Task WhileTheWinnerStreams_TheSupersededMastersNewerCopy_ReachesTheSlave()
    {
        var slave = Slave();
        var first = StreamedPush(1, new ClipboardPushMessage(BigText('a')));
        var second = StreamedPush(1, new ClipboardPushMessage(BigText('b')));
        await Begin(slave, first, M1);
        await Begin(slave, second, M2);
        await Chunk(slave, second.Chunks[0], M2);

        await Hash(slave, M1, new ClipboardHashMessage(TextHash("newer on the first")));
        var asked = slave.Sent.Of(MessageKind.ClipboardPullRequest, M1).Count;
        await Push(slave, M1, new ClipboardPushMessage("newer on the first"));
        await Chunks(slave, second, from: 1, host: M2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(asked, Is.EqualTo(1));
            Assert.That(AbortsTo(slave.Sent, M2), Is.EqualTo([1L]));
            Assert.That(_clipboard.Text, Is.EqualTo("newer on the first"));
            Assert.That(_osd.Shown.Count, Is.Zero, "a superseded stream is no failure");
        }
    }

    // the superseded master's newer Begin starts last, so it wins, silently
    [Test]
    public async Task WhileTheWinnerStreams_TheSupersededMastersNewerBegin_SupersedesTheWinner()
    {
        var slave = Slave();
        var first = StreamedPush(1, new ClipboardPushMessage(BigText('a')));
        var second = StreamedPush(1, new ClipboardPushMessage(BigText('b')));
        var third = StreamedPush(2, new ClipboardPushMessage(BigText('c')));
        await Begin(slave, first, M1);
        await Begin(slave, second, M2);
        await Chunk(slave, second.Chunks[0], M2);

        await Begin(slave, third, M1);
        await Chunks(slave, second, from: 1, host: M2);
        await Chunks(slave, third, host: M1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(AbortsTo(slave.Sent, M1), Is.EqualTo([1L]));
            Assert.That(AbortsTo(slave.Sent, M2), Is.EqualTo([1L]));
            Assert.That(_clipboard.Text, Is.EqualTo(BigText('c')));
            Assert.That(_osd.Shown.Count, Is.Zero);
        }
    }

    [Test]
    public async Task SelectionOnlyNeedFromOneMaster_DoesNotTouchTheOthersPush()
    {
        _clipboard.SetupText("shared");
        var slave = Slave();
        await Hash(slave, M1, new ClipboardHashMessage(TextHash("shared"), PrimaryHash: 0x5E1));
        Assert.That(slave.Sent.Of(MessageKind.ClipboardPullRequest, M1), Has.Count.EqualTo(1), "pre-condition: the first asks for its selection");

        // the same clipboard, no selection: applied whole, never as the first's selection-only push
        await Push(slave, M2, new ClipboardPushMessage("shared"));

        Assert.That(_clipboard.SetClipboardCallCount, Is.EqualTo(1));
    }

    [Test]
    public async Task SelectionOnlyNeedFromOneMaster_DoesNotKeepTheClipboardForTheOthersBegin()
    {
        _clipboard.SetupText("shared");
        var slave = Slave();
        await Hash(slave, M1, new ClipboardHashMessage(TextHash("shared"), PrimaryHash: 0x5E1));
        Assert.That(slave.Sent.Of(MessageKind.ClipboardPullRequest, M1), Has.Count.EqualTo(1), "pre-condition: the first asks for its selection");

        await Begin(slave, StreamedPush(1, new ClipboardPushMessage("shared" + BigText('x')), TextHash("shared")), M2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_clipboard.ClearClipboardCallCount, Is.EqualTo(1));
            Assert.That(_clipboard.Text, Is.Null);
        }
    }

    [Test]
    public async Task WinnerDepartsMidStream_ItsStreamFails_AndTheSupersededMasterMaySendAgain()
    {
        var slave = Slave();
        var first = StreamedPush(1, new ClipboardPushMessage(BigText('a')));
        var second = StreamedPush(1, new ClipboardPushMessage(BigText('b')));
        await Begin(slave, first, M1);
        await Begin(slave, second, M2);
        await Chunk(slave, second.Chunks[0], M2);

        await slave.Peers([M1]);
        var warning = await _log.WaitFor(e => e.Level == LogLevel.Warning && e.Message.Contains(M2));
        await Push(slave, M1, new ClipboardPushMessage("the first again"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(warning.Message, Does.Contain("dropped"));
            Assert.That(await _osd.Shown.Next("the failure"), Is.EqualTo(ClipboardStreamReceiver.FailedOsd));
            Assert.That(_osd.Shown.Count, Is.Zero, "only the departure is a failure");
            Assert.That(AbortsTo(slave.Sent, M1), Is.EqualTo([1L]));
            Assert.That(_clipboard.Text, Is.EqualTo("the first again"));
        }
    }

    // -- helpers --

    private TestableSlaveRelay Slave() => _slaves.New(_clipboard, _osd, _clock, _log);

    private static Task Push(TestableSlaveRelay slave, string host, ClipboardPushMessage push) =>
        slave.SimulateReceive(host, MessageKind.ClipboardPush, push.ToSaneJson());

    private static Task Hash(TestableSlaveRelay slave, string host, ClipboardHashMessage hash) =>
        slave.SimulateReceive(host, MessageKind.ClipboardHash, hash.ToSaneJson());
}
