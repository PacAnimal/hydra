using Cathedral.Extensions;
using Hydra.Config;
using Hydra.Relay;
using Hydra.Screen;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Tests.Setup;
using static Tests.Setup.ClipboardStreaming;

namespace Tests.Relay.Edge;

// slave states: dormancy mid-stream either way, a pending hash while dormant, waking mid-stream, file clipboards on the
// target, and the system clipboard mode between two macs; each checks the clipboard is never stale, the newest copy
// wins, and nothing pends forever
[TestFixture]
public class ClipboardEdgeSlaveStateTests : MasterClipboardFixture
{
    // a second master on the relay, whose pull shares the slave's stream loop
    private const string OtherMaster = "other-pc";

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
    public Task StopSlaves() => _slaves.StopAll();

    // -- dormant mid-receive: the master's push streaming in when the displays sleep --

    [TestCase(true)]
    [TestCase(false)]
    public async Task DormantMidReceive_StreamIsDroppedSilentlyAndTheNextEntryBringsTheMastersClipboard(bool chunkWhileDormant)
    {
        _clipboard.SetupText("old on the slave");
        var slave = Slave();
        var text = BigText('a');
        var stream = StreamedPush(1, new ClipboardPushMessage(text), TextHash(text));
        await Begin(slave, stream);
        await Chunk(slave, stream.Chunks[0]);

        await slave.Dormancy.Enter();
        if (chunkWhileDormant) await Chunk(slave, stream.Chunks[1]);
        // a stream still pending would fail here, on a dark screen
        _clock.Advance(ClipboardStreamReceiver.Watchdog * 2);
        var whileDormant = new { Shown = _osd.Shown.Count, _clipboard.Text, _clipboard.SetClipboardCallCount };

        await slave.Dormancy.Exit();
        await Chunks(slave, stream, from: chunkWhileDormant ? 2 : 1);
        var setByOld = _clipboard.SetClipboardCallCount;
        // the next entry: the master's hash, which a dropped stream no longer counts as applied
        await slave.SimulateReceive(Master, MessageKind.ClipboardHash, new ClipboardHashMessage(TextHash(text)).ToSaneJson());
        var asked = slave.Sent.Of(MessageKind.ClipboardPullRequest, Master).Count;
        var again = StreamedPush(2, new ClipboardPushMessage(text), TextHash(text));
        await Begin(slave, again);
        await Chunks(slave, again);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(whileDormant.Shown, Is.Zero, "no failure on a dark screen");
            Assert.That(whileDormant.Text, Is.Null, "honest empty, never the old clipboard");
            Assert.That(whileDormant.SetClipboardCallCount, Is.Zero);
            Assert.That(setByOld, Is.Zero, "the dropped stream's late chunks apply nothing");
            Assert.That(asked, Is.EqualTo(1));
            Assert.That(_clipboard.Text, Is.EqualTo(text));
            Assert.That(_osd.Shown.Count, Is.Zero);
        }
    }

    // -- dormant mid-send: the slave's pull answer streaming to the master when the displays sleep --

    [Test]
    public async Task DormantMidSend_TheOutgoingStreamStops()
    {
        var image = BigImage(1);
        _clipboard.SetupImage(image);
        var slave = Slave();
        slave.Reliable.Hold();
        await Pull(slave);
        await slave.Sent.WaitFor(MessageKind.ClipboardStreamBegin, 1, Master);
        var stopped = slave.Sent.Of<ClipboardStreamBeginMessage>(MessageKind.ClipboardStreamBegin).Single().Id;

        await slave.Dormancy.Enter();
        slave.Reliable.Release();

        // awake again, an answer to another master shares the loop in turn, so by its last chunk a live stream's are all sent
        await slave.Dormancy.Exit();
        await Pull(slave, 2, host: OtherMaster);
        var answer = await SentAnswer(slave, host: OtherMaster);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(slave.Sent.Of<ClipboardStreamChunkMessage>(MessageKind.ClipboardStreamChunk).Select(c => c.Id), Has.None.EqualTo(stopped));
            Assert.That(answer.PullId, Is.EqualTo(2));
            Assert.That(answer.ImagePng, Is.EqualTo(image));
        }
    }

    // the master's side of it: the answer stops arriving, so its receiver fails within the watchdog, and says so where the
    // cursor is, without waiting on that slave forever
    [TestCase(false)]
    [TestCase(true)]
    public async Task SlaveGoesDormantMidAnswer_TheMastersStreamFailsWithTheOsdWhereTheCursorIs(bool cursorOnAnotherHost)
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var (platform, relay, service) = await StartSides(clipboard, _clock, _osd);
        VisitAndLeaveRemote(platform);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), null, ImagePng: BigImage(2)));
        await Begin(relay, stream);
        await Chunk(relay, stream.Chunks[0]);
        if (cursorOnAnotherHost) EnterLeftHost(platform, relay);

        // the slave sends nothing more
        _clock.Advance(ClipboardStreamReceiver.Watchdog);
        await service.Flush();
        await service.HashReads();

        const string failed = ClipboardStreamReceiver.FailedOsd;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(clipboard.Text, Is.Null, "honest empty");
            Assert.That(clipboard.ImagePng, Is.Null);
            if (cursorOnAnotherHost)
            {
                Assert.That(relay.Sent.Of<OsdMessage>(MessageKind.Osd, "remote2").Select(o => o.Text), Is.EqualTo([failed]));
                Assert.That(_osd.Shown.Count, Is.Zero, "not on the master's screen, where the cursor isn't");
                Assert.That(Hashes(relay, "remote2"), Is.EqualTo([new ClipboardHashMessage(null, Pending: true)]));
            }
            else
            {
                Assert.That(await _osd.Shown.Next("the failure"), Is.EqualTo(failed));
                Assert.That(relay.Sent.Of(MessageKind.Osd), Is.Empty);
            }
        }
    }

    // -- a pending hash while dormant --

    [Test]
    public async Task Dormant_PendingHash_IsRefusedAndNothingPends()
    {
        _clipboard.SetupText("kept");
        var slave = Slave();
        await slave.Dormancy.Enter();

        await slave.SimulateReceive(Master, MessageKind.ClipboardHash, new ClipboardHashMessage(null, Pending: true).ToSaneJson());
        var whileDormant = new { _clipboard.ClearClipboardCallCount, _clipboard.Text };
        await slave.Dormancy.Exit();
        // awake, the master's hash of what it holds finds it there: nothing was left waiting on the refused pending
        await slave.SimulateReceive(Master, MessageKind.ClipboardHash, new ClipboardHashMessage(TextHash("kept")).ToSaneJson());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(whileDormant.ClearClipboardCallCount, Is.Zero);
            Assert.That(whileDormant.Text, Is.EqualTo("kept"));
            Assert.That(_clipboard.Text, Is.EqualTo("kept"));
            Assert.That(_clipboard.SetClipboardCallCount, Is.Zero);
            Assert.That(slave.Sent.Of(MessageKind.ClipboardPullRequest), Is.Empty);
        }
    }

    // -- waking mid-stream --

    [Test]
    public async Task WakingMidStream_TheNewStreamIsAcceptedAndCompletes()
    {
        _clipboard.SetupText("old on the slave");
        var slave = Slave();
        await slave.Dormancy.Enter();
        var refused = StreamedPush(1, new ClipboardPushMessage(BigText('a')));
        await Begin(slave, refused);
        await Chunk(slave, refused.Chunks[0]);

        await slave.Dormancy.Exit();
        await Chunks(slave, refused, from: 1);
        var fresh = StreamedPush(2, new ClipboardPushMessage(BigText('b')));
        await Begin(slave, fresh);
        await Chunks(slave, fresh);
        // a stream still pending would time out here
        _clock.Advance(ClipboardStreamReceiver.Watchdog);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_clipboard.Text, Is.EqualTo(BigText('b')));
            Assert.That(_clipboard.ClearClipboardCallCount, Is.EqualTo(1), "only the accepted stream's Begin empties");
            Assert.That(_clipboard.SetClipboardCallCount, Is.EqualTo(1));
            Assert.That(_osd.Shown.Count, Is.Zero);
        }
    }

    // -- the dormant slave the master pulls from on leaving --

    // the user is away, so nothing was copied: answered unchanged at once, whole, the big clipboard never streamed
    [Test]
    public async Task Dormant_Pull_IsAnsweredUnchangedWithoutStreaming()
    {
        _clipboard.SetupImage(BigImage(1));
        var slave = Slave();
        await slave.Dormancy.Enter();

        await Pull(slave);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(slave.Sent.Of<ClipboardPullResponseMessage>(MessageKind.ClipboardPullResponse, Master),
                Is.EqualTo([new ClipboardPullResponseMessage(1, null, Unchanged: true)]));
            Assert.That(slave.Sent.Of(MessageKind.ClipboardStreamBegin), Is.Empty);
        }
    }

    // a pull whose answer never comes (lost on the way): the next host's hash waits a second, not forever, and that
    // pull holds up no later pull or entry
    [TestCase(true)]
    [TestCase(false)]
    public async Task PullUnanswered_TheNextHostWaitsOnlyTheDeferralAndLaterPullsGoAhead(bool newCopyOnTheNextHost)
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("on the master");
        var (platform, relay, service) = await StartSides(clipboard, _clock);
        VisitAndLeaveRemote(platform);
        Assert.That(relay.Sent.Of(MessageKind.ClipboardPull, "remote"), Has.Count.EqualTo(1), "pre-condition: pulled from remote, which never answers");

        EnterLeftHost(platform, relay);
        var early = Hashes(relay, "remote2").Count;
        _clock.Advance(DeferredClipboardOffer.Wait);
        await service.Flush();
        await service.HashReads();
        var waitedOut = Hashes(relay, "remote2").Select(h => h.Hash).ToList();

        LeaveLeftHost(platform);
        var pulled = relay.Sent.Of(MessageKind.ClipboardPull, "remote2").Count;
        var answer = newCopyOnTheNextHost
            ? new ClipboardPullResponseMessage(PullIdTo(relay, "remote2"), "copied on remote2")
            : new ClipboardPullResponseMessage(PullIdTo(relay, "remote2"), null, Unchanged: true);
        await AnswerPull(relay, answer, "remote2");
        var expected = newCopyOnTheNextHost ? "copied on remote2" : "on the master";
        // back on, remote's pull, still unanswered, is not waited on again
        EnterLeftHost(platform, relay, 2);
        var atReentry = Hashes(relay, "remote2").Select(h => h.Hash).ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(early, Is.Zero);
            Assert.That(waitedOut, Is.EqualTo([TextHash("on the master")]));
            Assert.That(pulled, Is.EqualTo(1));
            Assert.That(clipboard.Text, Is.EqualTo(expected));
            Assert.That(atReentry, Is.EqualTo([TextHash("on the master"), TextHash(expected)]));
        }
    }

    // -- file clipboards on the target --

    [Test]
    public async Task StreamOntoCopiedFiles_TheFilesAreKeptAndOnlyTheSelectionTaken()
    {
        _clipboard.SetupText("report.pdf");
        _clipboard.HasFileClipboardValue = true;
        var slave = Slave();
        var stream = StreamedPush(1, new ClipboardPushMessage(BigText('a'), PrimaryText: "the selection"));

        await Begin(slave, stream);
        var atBegin = new { _clipboard.ClearClipboardCallCount, _clipboard.Text };
        await Chunks(slave, stream);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(atBegin.ClearClipboardCallCount, Is.Zero, "copied files are never emptied");
            Assert.That(atBegin.Text, Is.EqualTo("report.pdf"));
            Assert.That(_clipboard.SetClipboardCallCount, Is.Zero, "the files stay");
            Assert.That(_clipboard.Text, Is.EqualTo("report.pdf"));
            Assert.That(_clipboard.PrimaryText, Is.EqualTo("the selection"));
            Assert.That(_osd.Shown.Count, Is.Zero);
        }
    }

    [Test]
    public async Task FilesCopiedMidStream_TheStreamIsAbandonedAndTheFilesKept()
    {
        var slave = Slave();
        var stream = StreamedPush(3, new ClipboardPushMessage(BigText('a'), PrimaryText: "the selection"));
        await Begin(slave, stream);
        await Chunk(slave, stream.Chunks[0]);

        // the user's copy of files takes the clipboard from Hydra
        _clipboard.CanUseEchoFallbackValue = false;
        _clipboard.HasFileClipboardValue = true;
        _clipboard.SetupText("report.pdf");
        await Chunks(slave, stream, from: 1);
        _clock.Advance(ClipboardStreamReceiver.Watchdog);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(AbortsTo(slave.Sent, Master), Is.EqualTo([3L]));
            Assert.That(_clipboard.SetClipboardCallCount, Is.Zero);
            Assert.That(_clipboard.Text, Is.EqualTo("report.pdf"));
            Assert.That(_osd.Shown.Count, Is.Zero);
        }
    }

    [Test]
    public async Task PendingHash_OnAHostHoldingFiles_LeavesTheClipboardAlone()
    {
        _clipboard.SetupText("report.pdf");
        _clipboard.HasFileClipboardValue = true;
        var slave = Slave();

        await slave.SimulateReceive(Master, MessageKind.ClipboardHash, new ClipboardHashMessage(null, Pending: true).ToSaneJson());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_clipboard.ClearClipboardCallCount, Is.Zero);
            Assert.That(_clipboard.SetClipboardCallCount, Is.Zero);
            Assert.That(_clipboard.Text, Is.EqualTo("report.pdf"));
            Assert.That(slave.Sent.Of(MessageKind.ClipboardPullRequest), Is.Empty);
        }
    }

    // -- the system clipboard between two macs: Hydra stands down, so nothing streams either way --

    [Test]
    public async Task SystemClipboardMacToMac_TheMasterStreamsNothing()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupImage(BigImage(1));
        var (platform, relay, service) = await StartMaster(clipboard, PeerPlatform.MacOs, SystemSync, PeerPlatform.MacOs);

        VisitAndLeaveRemote(platform);
        TransitionTestHelper.EnterRemote(platform);
        await SimulatePullRequest(relay);
        await service.Flush();
        await service.HashReads();

        Assert.That(relay.Sent.Snapshot().Where(s => MessageKinds.IsClipboard(s.Kind)), Is.Empty);
    }

    [Test]
    public async Task SystemClipboardMacToMac_AStreamFromTheSlaveIsNotAccepted()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("handled by Universal Clipboard");
        var (platform, relay, service) = await StartMaster(clipboard, PeerPlatform.MacOs, SystemSync, PeerPlatform.MacOs);
        VisitAndLeaveRemote(platform);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(1, null, ImagePng: BigImage(2)));

        await Begin(relay, stream);
        await Chunks(relay, stream);
        await Abort(relay, 1);
        await service.Flush();
        await service.HashReads();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(clipboard.ClearClipboardCallCount, Is.Zero);
            Assert.That(clipboard.SetClipboardCallCount, Is.Zero);
            Assert.That(clipboard.Text, Is.EqualTo("handled by Universal Clipboard"));
            Assert.That(relay.Sent.Snapshot().Where(s => MessageKinds.IsClipboard(s.Kind)), Is.Empty);
        }
    }

    // -- dormancy over before its queued drop runs --

    // the worker busy with a read while the displays sleep and wake, a Begin queued before the sleep runs awake: the drop
    // queued behind it finds the slave awake and drops nothing, so the stream lands
    [Test]
    public async Task DormantAndAwakeAgainWhileTheWorkerIsBusy_AStreamBegunAfterTheWake_StillLands()
    {
        _clipboard.SetupText("old on the slave");
        var dormancy = new DormancyState(NullLogger<DormancyState>.Instance);
        var slave = _slaves.Add(new TestableSlaveRelay(clipboard: _clipboard, osd: _osd, clock: _clock, dormancy: dormancy));
        var text = BigText('a');
        var stream = StreamedPush(1, new ClipboardPushMessage(text), TextHash(text));
        _clipboard.ReadGate.Hold();
        await slave.Deliver(Master, MessageKind.ClipboardHash, new ClipboardHashMessage(TextHash(text)).ToSaneJson());
        await _clipboard.ReadsParked.Next("the hash's read");
        await slave.Deliver(Master, MessageKind.ClipboardStreamBegin, stream.Begin.ToSaneJson());

        // straight on the dormancy, since the slave's own waits for the parked worker
        await dormancy.Enter();
        await dormancy.Exit();
        _clipboard.ReadGate.Release();
        await slave.ClipboardIdle();
        await Chunks(slave, stream);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(slave.Sent.Of(MessageKind.ClipboardPullRequest, Master), Has.Count.EqualTo(1), "pre-condition: the hash asked for the push");
            Assert.That(_clipboard.Text, Is.EqualTo(text), "the stream begun after the wake lands");
            Assert.That(slave.Sent.Of(MessageKind.ClipboardStreamAbort), Is.Empty);
            Assert.That(_osd.Shown.Count, Is.Zero);
        }
    }

    // -- helpers --

    private static readonly IHydraProfile SystemSync = TransitionTestHelper.ProfileWith(clipboardSync: ClipboardSyncMode.System);


    private TestableSlaveRelay Slave() => _slaves.New(_clipboard, _osd, _clock);
}
