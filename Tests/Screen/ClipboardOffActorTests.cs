using Hydra.Keyboard;
using Hydra.Relay;
using Hydra.Screen;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Tests.Setup;
using static Tests.Setup.ClipboardStreaming;

namespace Tests.Screen;

// the master's clipboard is never read on the input actor. The entry hash is read in the background and sent
// only to the host the cursor is still on, for its current visit; a pull goes against the last known clipboard
[TestFixture]
public class ClipboardOffActorTests : MasterClipboardFixture
{
    private const int KeyA = 30;

    private readonly List<FakeClipboardSync> _clipboards = [];

    // a read still parked would outlive the test
    [TearDown]
    public void ReleaseReads()
    {
        foreach (var clipboard in _clipboards) clipboard.ReadGate.Release();
        _clipboards.Clear();
    }

    [Test]
    public async Task ReadHung_InputStillReachesTheSlaveAndTheCursorCrossesToAnother()
    {
        var clipboard = Clipboard("on the master");
        var ticks = new SteppableTicks();
        var (platform, relay, _) = await Start(clipboard, getTickCount: ticks.Now);
        clipboard.ReadGate.Hold();
        TransitionTestHelper.EnterRemote(platform);

        ticks.Advance(20); // past the mouse throttle
        platform.FireMouseDelta(10, 0);
        platform.FireKeyEvent(KeyEvent.Char(KeyEventType.KeyDown, 'a', KeyModifiers.None) with { PhysicalKey = KeyA });
        platform.FireKeyEvent(KeyEvent.Char(KeyEventType.KeyUp, 'a', KeyModifiers.None) with { PhysicalKey = KeyA });
        var moves = relay.Sent.Snapshot().Where(s => s.Kind is MessageKind.MouseMove or MessageKind.MouseMoveDelta).SelectMany(s => s.Targets).ToList();
        var keys = relay.Sent.Of<KeyEventMessage>(MessageKind.KeyEvent, "remote");
        TransitionTestHelper.MoveRightTo(platform, relay, "remote2");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(clipboard.ReadGate.IsHeld, Is.True, "pre-condition: the entry's read is parked");
            Assert.That(moves, Is.Not.Empty.And.All.EqualTo("remote"), "the mouse reaches the slave");
            Assert.That(keys.Select(k => k.Type), Is.EqualTo([KeyEventType.KeyDown, KeyEventType.KeyUp]), "the keys reach the slave");
            Assert.That(relay.Sent.Of(MessageKind.EnterScreen, "remote2"), Has.Count.EqualTo(1), "the cursor crossed to the second slave");
            Assert.That(relay.Sent.Of(MessageKind.ClipboardHash), Is.Empty, "no hash while the read is parked");
        }
    }

    [Test]
    public async Task ReadReleasedAfterCrossing_TheHashGoesOnlyToTheHostTheCursorIsOn()
    {
        var clipboard = Clipboard("on the master");
        var log = new CapturingLog<InputRouter>(LogLevel.Debug);
        var (platform, relay, _) = await Start(clipboard, log: log);
        clipboard.ReadGate.Hold();
        TransitionTestHelper.EnterRemote(platform);
        TransitionTestHelper.MoveRightTo(platform, relay, "remote2");
        // remote2's hash waits on the pull to remote, which finds nothing new there
        await AnswerUnchanged(relay, "remote");

        clipboard.ReadGate.Release();
        await relay.Sent.WaitFor(MessageKind.ClipboardHash, 1, "remote2");
        await log.WaitFor(e => e.Message == "Clipboard hash to remote dropped: outdated");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(relay.Sent.Of(MessageKind.ClipboardHash, "remote"), Is.Empty, "the host the cursor left gets no hash");
            Assert.That(relay.Sent.Of<ClipboardHashMessage>(MessageKind.ClipboardHash, "remote2").Select(h => h.Hash),
                Is.EqualTo(new ulong?[] { TextHash("on the master") }));
        }
    }

    [Test]
    public async Task ReadReleasedAfterLeavingAndReentering_ExactlyOneHashGoesForTheCurrentVisit()
    {
        var clipboard = Clipboard("on the master");
        var log = new CapturingLog<InputRouter>(LogLevel.Debug);
        var (platform, relay, _) = await Start(clipboard, log: log, chain: false);
        clipboard.ReadGate.Hold();
        VisitAndLeaveRemote(platform);
        var pulls = relay.Sent.Of(MessageKind.ClipboardPull, "remote").Count;
        TransitionTestHelper.EnterRemote(platform);
        var entries = relay.Sent.Of(MessageKind.EnterScreen, "remote").Count;

        clipboard.ReadGate.Release();
        await relay.Sent.WaitFor(MessageKind.ClipboardHash, 1, "remote");
        await log.WaitFor(e => e.Message == "Clipboard hash to remote dropped: outdated");
        var sent = relay.Sent.Snapshot().Where(s => s.Kind is MessageKind.EnterScreen or MessageKind.ClipboardHash).Select(s => s.Kind).ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pulls, Is.EqualTo(1), "pre-condition: the visit was left");
            Assert.That(entries, Is.EqualTo(2), "pre-condition: re-entered");
            Assert.That(relay.Sent.Of<ClipboardHashMessage>(MessageKind.ClipboardHash, "remote").Select(h => h.Hash),
                Is.EqualTo(new ulong?[] { TextHash("on the master") }), "one hash, the first visit's dropped");
            Assert.That(sent, Is.EqualTo([MessageKind.EnterScreen, MessageKind.EnterScreen, MessageKind.ClipboardHash]),
                "the hash follows the current visit's entry");
        }
    }

    [Test]
    public async Task LeavingASlave_PullsAgainstTheKnownClipboardWithoutReadingIt()
    {
        var clipboard = Clipboard("on the master");
        var (platform, relay, _) = await Start(clipboard, chain: false);
        TransitionTestHelper.EnterRemote(platform);
        await relay.Sent.WaitFor(MessageKind.ClipboardHash, 1, "remote");
        var reads = clipboard.ReadCallCount;

        LeaveRemote(platform);

        var pulls = relay.Sent.Of<ClipboardPullMessage>(MessageKind.ClipboardPull, "remote");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(platform.IsOnVirtualScreen, Is.False, "pre-condition: back on the master");
            Assert.That(clipboard.ReadCallCount, Is.EqualTo(reads), "the pull reads nothing");
            Assert.That(pulls.Select(p => p.MasterHash), Is.EqualTo(new ulong?[] { TextHash("on the master") }), "against the entry's read");
        }
    }

    [Test]
    public async Task ReadFinishingAfterLeaveScreenInACrossing_SendsNoHashToTheHostLeft()
    {
        var clipboard = Clipboard("on the master");
        var (platform, relay, service) = await Start(clipboard);
        clipboard.ReadGate.Hold();
        TransitionTestHelper.EnterRemote(platform);
        await clipboard.ReadsParked.Next("the entry's read");
        // the read for remote finishes between its LeaveScreen and remote2's entry
        var readFinished = false;
        relay.Sending = (kind, targets) =>
        {
            if (kind != MessageKind.LeaveScreen || !targets.Contains("remote")) return;
            relay.Sending = null;
            clipboard.ReadGate.Release();
            // asserted below: a failure here, on the router's input actor, may be swallowed
            readFinished = service.HashReads().Wait(TestBound.Bound);
        };

        TransitionTestHelper.MoveRightTo(platform, relay, "remote2");
        await service.HashReads();

        var toRemote = relay.Sent.Snapshot().Where(s => s.Targets.Contains("remote")).Select(s => s.Kind).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(readFinished, Is.True, "the entry's read finished");
            Assert.That(toRemote, Does.Contain(MessageKind.LeaveScreen), "pre-condition: remote was left");
            Assert.That(toRemote.SkipWhile(k => k != MessageKind.LeaveScreen), Does.Not.Contain(MessageKind.ClipboardHash), "no hash after its LeaveScreen");
        }
    }

    // told Pending and owed its hash once the stream landed, it is sent it though the cursor left it mid-read
    [Test]
    public async Task OwedReadFinishingAfterTheCursorLeftItsHost_StillSendsThatHostItsHash()
    {
        var clipboard = Clipboard("old on the master");
        var (platform, relay, service) = await Start(clipboard);
        TransitionTestHelper.EnterRemote(platform);
        TransitionTestHelper.MoveRightTo(platform, relay, "remote2");
        var image = BigImage(2);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), null, ImagePng: image));
        await Begin(relay, stream);
        await service.HashReads();
        var told = relay.Sent.Of<ClipboardHashMessage>(MessageKind.ClipboardHash, "remote2");

        clipboard.ReadGate.Hold();
        await Chunks(relay, stream);
        await clipboard.ReadsParked.Next("the owed read");
        for (var i = 0; i < 40 && relay.Sent.Of(MessageKind.EnterScreen, "remote").Count < 2; i++)
            platform.FireMouseDelta(-100, 0);
        var left = relay.Sent.Of(MessageKind.LeaveScreen, "remote2").Count;
        clipboard.ReadGate.Release();
        await service.HashReads();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(told, Is.EqualTo([new ClipboardHashMessage(null, Pending: true)]), "pre-condition: told Pending");
            Assert.That(left, Is.EqualTo(1), "pre-condition: remote2 was left mid-read");
            Assert.That(clipboard.ImagePng, Is.EqualTo(image), "pre-condition: the stream landed");
            Assert.That(relay.Sent.Of<ClipboardHashMessage>(MessageKind.ClipboardHash, "remote2").Select(h => h.Hash),
                Is.EqualTo(new ulong?[] { null, ImageHash(image) }), "the owed hash still goes");
        }
    }

    // the owed read under way as the cursor leaves its host, which pulls from it, and the host answers with a copy of its
    // own before that read ends: the read is older than the pull and that answer, so it never reaches the host with the
    // older clipboard, which would empty the copy it just answered with; a fresh owed read follows the pull instead
    [Test]
    public async Task OwedReadUnderWayAsItsHostIsPulled_ItNeverOffersTheHostAClipboardOlderThanItsAnswer()
    {
        var clipboard = Clipboard("old on the master");
        var bundle = await StartSides(clipboard, new FakeTimeProvider(), flushActorOnly: true);
        var (platform, relay, service) = bundle;
        await OwedReadParkedOnTheLeftHost(clipboard, bundle);

        LeaveLeftHost(platform);
        await AnswerPull(relay, new ClipboardPullResponseMessage(PullIdTo(relay, "remote2"), "copied on remote2"), "remote2");
        await service.Flush();
        clipboard.ReadGate.Release();
        await service.HashReads();

        var toLeft = SentTo(relay, "remote2");
        var pull = toLeft.FindLastIndex(s => s.Kind == MessageKind.ClipboardPull);
        var afterThePull = Hashes(relay, "remote2").Skip(1).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(clipboard.Text, Is.EqualTo("copied on remote2"), "pre-condition: its answer was taken");
            Assert.That(toLeft.Take(pull).Count(s => s.Kind == MessageKind.ClipboardHash), Is.EqualTo(1), "only Pending before the pull");
            Assert.That(afterThePull.Select(h => h.Hash), Is.EqualTo([TextHash("copied on remote2")]), "never the older clipboard");
        }
    }

    // the owed read under way as the cursor leaves its host, which pulls from it, finishing before the host's answer
    // reaches the master: that read began before the pull, so it is outdated and sends nothing; a fresh owed read, begun
    // after the pull, goes instead, carrying the pull as ResendFrom, so the host keeps the copy it answered with
    [Test]
    public async Task OwedReadFinishingAfterItsHostIsPulled_BeforeItsAnswer_OnlyAFreshReadAfterThePullGoes()
    {
        var clipboard = Clipboard("old on the master");
        var bundle = await StartSides(clipboard, new FakeTimeProvider(), flushActorOnly: true);
        var (platform, relay, service) = bundle;
        var image = await OwedReadParkedOnTheLeftHost(clipboard, bundle);

        LeaveLeftHost(platform);
        var pullId = PullIdTo(relay, "remote2");
        await clipboard.ReadsParked.Next("the fresh owed read, begun after the pull");
        clipboard.ReadGate.Release();
        await service.HashReads();
        var toLeft = SentTo(relay, "remote2");
        var pull = toLeft.FindLastIndex(s => s.Kind == MessageKind.ClipboardPull);
        var afterThePull = Hashes(relay, "remote2").Skip(1).ToList();

        await AnswerPull(relay, new ClipboardPullResponseMessage(pullId, "copied on remote2"), "remote2");
        await service.Flush();
        await service.HashReads();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(toLeft.Take(pull).Count(s => s.Kind == MessageKind.ClipboardHash), Is.EqualTo(1), "only Pending before the pull");
            Assert.That(afterThePull, Is.EqualTo([new ClipboardHashMessage(ImageHash(image), Visit: afterThePull[0].Visit, ResendFrom: pullId)]),
                "one hash after the pull, the fresh read's, asking remote2 to keep its answer");
            Assert.That(clipboard.Text, Is.EqualTo("copied on remote2"), "its answer was taken");
            Assert.That(Hashes(relay, "remote2").Skip(1), Is.EqualTo(afterThePull), "nothing more after the answer");
        }
    }

    // re-entered mid owed read, its entry's read is owed in its place: a flick leave still sends it
    [Test]
    public async Task ReenteredWhileItsOwedReadIsUnderWay_AFlickLeave_StillSendsItsHash()
    {
        var clipboard = Clipboard("old on the master");
        var bundle = await StartSides(clipboard, new FakeTimeProvider(), flushActorOnly: true);
        var (platform, relay, service) = bundle;
        var image = await OwedReadParkedOnTheLeftHost(clipboard, bundle);
        LeaveLeftHost(platform);

        EnterLeftHostFromTheMiddle(platform, relay, 2);
        await clipboard.ReadsParked.Next("the entry's read");
        LeaveLeftHost(platform);
        clipboard.ReadGate.Release();
        await service.HashReads();

        Assert.That(Hashes(relay, "remote2").Select(h => h.Hash), Is.EqualTo(new ulong?[] { null, ImageHash(image) }), "the owed hash still goes");
    }

    // re-entered mid owed read while a pull to another host is outstanding, its entry's wait is owed in its place: sent
    // once that pull is answered, though the cursor left
    [Test]
    public async Task ReenteredWhileItsOwedReadIsUnderWay_WaitingOnAnotherPull_ItIsSentItsHashOnceThatPullIsAnswered()
    {
        var clipboard = Clipboard("old on the master");
        var bundle = await StartSides(clipboard, new FakeTimeProvider(), flushActorOnly: true);
        var (platform, relay, service) = bundle;
        var image = await OwedReadParkedOnTheLeftHost(clipboard, bundle);
        LeaveLeftHost(platform);
        // onto remote, which waits on remote2's pull, and off it again, which pulls from it
        VisitAndLeaveRemote(platform);

        EnterLeftHostFromTheMiddle(platform, relay, 2);
        var atReentry = Hashes(relay, "remote2");
        LeaveLeftHost(platform);
        clipboard.ReadGate.Release();
        await service.HashReads();
        await AnswerUnchanged(relay, "remote");
        await service.Flush();
        await service.HashReads();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(atReentry, Is.EqualTo([new ClipboardHashMessage(null, Pending: true)]), "pre-condition: its entry's hash waits");
            Assert.That(Hashes(relay, "remote2").Select(h => h.Hash), Is.EqualTo(new ulong?[] { null, ImageHash(image) }), "the owed hash still goes");
        }
    }

    [Test]
    public async Task Stopping_WaitsForTheReadUnderWay_WhichSendsNothing()
    {
        var clipboard = Clipboard("on the master");
        var log = new CapturingLog<InputRouter>(LogLevel.Debug);
        var (platform, relay, service) = await Start(clipboard, log: log, chain: false);
        clipboard.ReadGate.Hold();
        TransitionTestHelper.EnterRemote(platform);
        await clipboard.ReadsParked.Next("the entry's read");

        var stop = Services.Stop(service);
        await log.WaitFor(e => e.Message == "Waiting for the clipboard hash reads under way");
        var stoppedWhileParked = stop.IsCompleted;
        clipboard.ReadGate.Release();
        await stop;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stoppedWhileParked, Is.False, "the stop waits for the read");
            Assert.That(service.HashReads().IsCompleted, Is.True, "no read outlives the stop");
            Assert.That(relay.Sent.Of(MessageKind.ClipboardHash), Is.Empty, "a read finishing while stopping sends nothing");
        }
    }

    [Test]
    public async Task Stopping_AReadThatNeverFinishes_IsWaitedOutAndLogged()
    {
        var clipboard = Clipboard("on the master");
        var time = new FakeTimeProvider();
        var log = new CapturingLog<InputRouter>(LogLevel.Debug);
        var (platform, relay, service) = await Start(clipboard, log: log, chain: false, time: time);
        clipboard.ReadGate.Hold();
        TransitionTestHelper.EnterRemote(platform);
        await clipboard.ReadsParked.Next("the entry's read");

        var stop = Services.Stop(service);
        await log.WaitFor(e => e.Message == "Waiting for the clipboard hash reads under way");
        time.Advance(TimeSpan.FromMinutes(1));
        await stop;
        await log.WaitFor(e => e.Level == LogLevel.Warning && e.Message.StartsWith("Clipboard hash reads still under way", StringComparison.Ordinal));
        clipboard.ReadGate.Release();
        await service.HashReads();

        Assert.That(relay.Sent.Of(MessageKind.ClipboardHash), Is.Empty, "the read left behind sends nothing");
    }

    // C4: a read begun before an apply or clear, finishing after it, leaves the newer state
    [Test]
    public async Task ReadBegunBeforeAClear_FinishingAfterIt_LeavesTheClearKnown()
    {
        var clipboard = Clipboard("old");
        var master = new ClipboardMaster(clipboard, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        clipboard.ReadGate.Hold();
        var read = Task.Run(master.Hash);
        await clipboard.ReadsParked.Next("the read");

        master.Empty();
        clipboard.ReadGate.Release();
        await read;

        Assert.That(master.Pull("remote").MasterHash, Is.Not.EqualTo(TextHash("old")), "pulls against the clear, not the older read");
    }

    private FakeClipboardSync Clipboard(string text)
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText(text);
        _clipboards.Add(clipboard);
        return clipboard;
    }

    // the router's fire callback flushes the actor alone, never waiting on a parked read
    private async Task<TestServiceBundle> Start(FakeClipboardSync clipboard, Func<long>? getTickCount = null, ILogger<InputRouter>? log = null,
        bool chain = true, FakeTimeProvider? time = null)
    {
        var bundle = TransitionTestHelper.CreateService(getTickCount: getTickCount, timeProvider: time ?? new FakeTimeProvider(),
            profile: chain ? TransitionTestHelper.ChainConfig : TransitionTestHelper.TestConfig, log: log, clipboard: clipboard);
        bundle.Platform.AfterFireCallback = bundle.Service.Flush;
        return await Services.StartOnline(bundle, chain ? ["remote", "remote2"] : ["remote"]);
    }

    // remote's answer streams in while the cursor is on remote2, told Pending, and lands with the read gate held, so
    // remote2's owed read parks, the cursor still there; the image that landed
    private static async Task<byte[]> OwedReadParkedOnTheLeftHost(FakeClipboardSync clipboard, TestServiceBundle bundle)
    {
        var (platform, relay, service) = bundle;
        VisitAndLeaveRemote(platform);
        await service.HashReads();
        var image = BigImage(2);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), null, ImagePng: image));
        await Begin(relay, stream);
        await Chunk(relay, stream.Chunks[0]);
        EnterLeftHost(platform, relay);
        await service.HashReads();
        Assert.That(Hashes(relay, "remote2"), Is.EqualTo([new ClipboardHashMessage(null, Pending: true)]), "pre-condition: told Pending");

        clipboard.ReadGate.Hold();
        await Chunks(relay, stream, from: 1);
        await clipboard.ReadsParked.Next("the owed read");
        Assert.That(clipboard.ImagePng, Is.EqualTo(image), "pre-condition: the stream landed");
        return image;
    }
}
