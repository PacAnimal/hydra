using Cathedral.Extensions;
using Hydra.Relay;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Tests.Setup;
using static Tests.Setup.ClipboardStreaming;

namespace Tests.Relay.Edge;

// peer loss: a host that leaves or a relay that drops mid-stream stops its streams both ways and nothing it sends later
// applies, so departures are clean, and whoever was left emptied or Pending is told, never pending forever and never
// handed a stale clipboard
[TestFixture]
public class ClipboardEdgePeerLossTests : MasterClipboardFixture
{
    private readonly SlaveRelays _slaves = new();

    [TearDown]
    public Task StopSlaves() => _slaves.StopAll();

    // -- 1. A leaves while its answer streams to the master, the cursor on C, which was told Pending --

    [Test]
    public async Task ALeavesMidAnswer_CursorOnC_NothingAppliesTheMasterStaysEmpty_AndCIsToldWithNoStaleHash()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var clock = new FakeTimeProvider();
        var (platform, relay, service) = await StartSides(clipboard, clock);
        VisitAndLeaveRemote(platform);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), null, ImagePng: BigImage(2)));
        await Begin(relay, stream);
        await Chunk(relay, stream.Chunks[0]);
        EnterLeftHostFromTheMiddle(platform, relay);
        var told = Hashes(relay, "remote2");

        await relay.FirePeersChanged("remote2");
        await Chunks(relay, stream, from: 1);
        clock.Advance(ClipboardStreamReceiver.Watchdog);
        await service.Flush();
        await service.HashReads();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(told, Is.EqualTo([new ClipboardHashMessage(null, Pending: true)]), "pre-condition: C told Pending");
            Assert.That(platform.IsOnVirtualScreen, Is.True, "the cursor stays on C");
            Assert.That(clipboard.SetClipboardCallCount, Is.Zero);
            Assert.That(clipboard.Text, Is.Null);
            Assert.That(clipboard.ImagePng, Is.Null);
            Assert.That(Hashes(relay, "remote2").Select(h => h.Hash), Has.None.EqualTo(TextHash("old on the master")));
            // the user sitting at C is told the clipboard didn't arrive
            Assert.That(OsdsTo(relay, "remote2"), Is.EqualTo([ClipboardStreamReceiver.FailedOsd]));
        }
    }

    // -- 2. the same, the cursor on the master's own screen --

    [Test]
    public async Task ALeavesMidAnswer_CursorOnTheMaster_NothingAppliesTheMasterStaysEmpty_AndTheMasterIsTold()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var osd = new RecordingOsd();
        var clock = new FakeTimeProvider();
        var (platform, relay, service) = await StartSingle(clipboard, osd, clock);
        VisitAndLeaveRemote(platform);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay), null, ImagePng: BigImage(2)));
        await Begin(relay, stream);
        await Chunk(relay, stream.Chunks[0]);

        await relay.FirePeersChanged();
        await Chunks(relay, stream, from: 1);
        clock.Advance(ClipboardStreamReceiver.Watchdog);
        await service.Flush();
        await service.HashReads();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(clipboard.SetClipboardCallCount, Is.Zero);
            Assert.That(clipboard.Text, Is.Null);
            Assert.That(clipboard.ImagePng, Is.Null);
            Assert.That(await Shown(osd), Is.EqualTo([ClipboardStreamReceiver.FailedOsd]));
        }
    }

    // -- 3. the same, the cursor back on A when it leaves, which returns the cursor home --

    [Test]
    public async Task ALeavesMidAnswerWithTheCursorOnIt_TheCursorReturnsHome_NothingApplies_AndTheMasterIsTold()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var osd = new RecordingOsd();
        var clock = new FakeTimeProvider();
        var (platform, relay, service) = await StartSingle(clipboard, osd, clock);
        VisitAndLeaveRemote(platform);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay), null, ImagePng: BigImage(2)));
        await Begin(relay, stream);
        await Chunk(relay, stream.Chunks[0]);
        TransitionTestHelper.EnterRemote(platform);

        // neither the departure nor the late chunks may throw
        await relay.FirePeersChanged();
        await Chunks(relay, stream, from: 1);
        clock.Advance(ClipboardStreamReceiver.Watchdog);
        await service.Flush();
        await service.HashReads();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(platform.IsOnVirtualScreen, Is.False, "A's departure returns the cursor home");
            Assert.That(clipboard.SetClipboardCallCount, Is.Zero);
            Assert.That(clipboard.Text, Is.Null);
            Assert.That(clipboard.ImagePng, Is.Null);
            Assert.That(await Shown(osd), Is.EqualTo([ClipboardStreamReceiver.FailedOsd]));
        }
    }

    // -- 4. the target of a push leaves mid-push --

    [Test]
    public async Task TargetLeavesMidPush_NoFurtherChunksGoToIt_AndAStreamToAnotherHostCompletes()
    {
        var clipboard = new FakeClipboardSync();
        var image = BigImage(1);
        clipboard.SetupImage(image);
        var (platform, relay, _) = await StartChain(clipboard, new FakeTimeProvider());
        TransitionTestHelper.EnterRemote(platform);
        relay.Reliable.Hold();
        await SimulatePullRequest(relay);
        await relay.Sent.WaitFor(MessageKind.ClipboardStreamBegin, 1, "remote");
        TransitionTestHelper.MoveRightTo(platform, relay, "remote2");
        await SimulatePullRequest(relay, "remote2");

        await relay.FirePeersChanged("remote2");
        relay.Reliable.Release();
        var push = await SentPush(relay, "remote2", begins: 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(push.ImagePng, Is.EqualTo(image));
            Assert.That(relay.Sent.Of(MessageKind.ClipboardStreamChunk, "remote"), Is.Empty);
        }
    }

    // -- 5. C leaves while Pending --

    [Test]
    public async Task CLeavesWhilePending_NothingGoesToItWhenAsStreamLands_AndOnItsReturnItGetsTheCurrentHash()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var (platform, relay, service) = await StartSides(clipboard, new FakeTimeProvider());
        VisitAndLeaveRemote(platform);
        var image = BigImage(2);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), null, ImagePng: image));
        await Begin(relay, stream);
        await Chunk(relay, stream.Chunks[0]);
        EnterLeftHostFromTheMiddle(platform, relay);

        await relay.FirePeersChanged("remote");
        var sentToCWhenItLeft = SentTo(relay, "remote2").Count;
        await Chunks(relay, stream, from: 1);
        await service.Flush();
        await service.HashReads();
        var sentToCAfterTheStream = SentTo(relay, "remote2").Count;
        await TransitionTestHelper.BringHostsOnline(relay, ["remote", "remote2"]);
        EnterLeftHostFromTheMiddle(platform, relay, entries: 2);

        var hashes = Hashes(relay, "remote2");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(platform.IsOnVirtualScreen, Is.True, "pre-condition: back on C");
            Assert.That(clipboard.ImagePng, Is.EqualTo(image));
            Assert.That(sentToCAfterTheStream, Is.EqualTo(sentToCWhenItLeft));
            Assert.That(hashes, Has.Count.EqualTo(2));
            Assert.That(hashes[0], Is.EqualTo(new ClipboardHashMessage(null, Pending: true)));
            Assert.That(hashes[^1].Hash, Is.EqualTo(ImageHash(image)));
            Assert.That(hashes[^1].Pending, Is.Null);
        }
    }

    // -- 6. the relay disconnects mid-stream, each direction --

    [Test]
    public async Task RelayDisconnectsMidPush_TheStreamStops_AndAfterReconnectAFreshPushRunsEndToEnd()
    {
        var clipboard = new FakeClipboardSync();
        var image = BigImage(1);
        clipboard.SetupImage(image);
        var (platform, relay, _) = await StartSingle(clipboard, clock: new FakeTimeProvider());
        TransitionTestHelper.EnterRemote(platform);
        relay.Reliable.Hold();
        await SimulatePullRequest(relay);
        await relay.Sent.WaitFor(MessageKind.ClipboardStreamBegin, 1, "remote");
        var dropped = relay.Sent.Of<ClipboardStreamBeginMessage>(MessageKind.ClipboardStreamBegin, "remote").Single().Id;

        await relay.FireDisconnected();
        relay.Reliable.Release();
        await TransitionTestHelper.BringRemoteOnline(relay);
        TransitionTestHelper.EnterRemote(platform);
        await SimulatePullRequest(relay);
        var push = await SentPush(relay, "remote", begins: 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(push.ImagePng, Is.EqualTo(image));
            Assert.That(relay.Sent.Of<ClipboardStreamChunkMessage>(MessageKind.ClipboardStreamChunk).Select(c => c.Id), Has.None.EqualTo(dropped));
        }
    }

    [Test]
    public async Task RelayDisconnectsMidAnswer_ItsLateChunksApplyNothing_AndAfterReconnectAFreshAnswerLands()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var (platform, relay, service) = await StartSingle(clipboard, clock: new FakeTimeProvider());
        VisitAndLeaveRemote(platform);
        var old = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay), null, ImagePng: BigImage(2)));
        await Begin(relay, old);
        await Chunk(relay, old.Chunks[0]);

        await relay.FireDisconnected();
        await Chunk(relay, old.Chunks[1]);
        var whileDown = new { clipboard.Text, clipboard.ImagePng, clipboard.SetClipboardCallCount };
        await TransitionTestHelper.BringRemoteOnline(relay);
        VisitAndLeaveRemote(platform);
        var image = BigImage(3);
        var fresh = StreamedAnswer(2, new ClipboardPullResponseMessage(PullIdTo(relay), null, ImagePng: image));
        await Begin(relay, fresh);
        await Chunk(relay, fresh.Chunks[0]);
        // a late chunk of the old stream, between the fresh one's
        await Chunk(relay, old.Chunks[2]);
        await Chunks(relay, fresh, from: 1);
        await service.Flush();
        await service.HashReads();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(whileDown.Text, Is.Null);
            Assert.That(whileDown.ImagePng, Is.Null);
            Assert.That(whileDown.SetClipboardCallCount, Is.Zero);
            Assert.That(clipboard.ImagePng, Is.EqualTo(image));
        }
    }

    [Test]
    public async Task RelayDisconnectsMidAnswer_CursorOnTheMaster_ItStaysHonestlyEmpty_AndTheMasterIsTold()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var osd = new RecordingOsd();
        var clock = new FakeTimeProvider();
        var (platform, relay, service) = await StartSingle(clipboard, osd, clock);
        VisitAndLeaveRemote(platform);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay), null, ImagePng: BigImage(2)));
        await Begin(relay, stream);
        await Chunk(relay, stream.Chunks[0]);

        await relay.FireDisconnected();
        await Chunks(relay, stream, from: 1);
        clock.Advance(ClipboardStreamReceiver.Watchdog);
        await service.Flush();
        await service.HashReads();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(clipboard.SetClipboardCallCount, Is.Zero);
            Assert.That(clipboard.Text, Is.Null);
            Assert.That(clipboard.ImagePng, Is.Null);
            Assert.That(await Shown(osd), Is.EqualTo([ClipboardStreamReceiver.FailedOsd]));
        }
    }

    // -- 7. A reconnects under the same name mid-stream: the relay's displacement broadcast, leaving then arriving --

    [Test]
    public async Task AReconnectsUnderTheSameNameMidAnswer_TheOldStreamIsNotResumed_AndItsNextAnswerLands()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var (platform, relay, service) = await StartSingle(clipboard, clock: new FakeTimeProvider());
        VisitAndLeaveRemote(platform);
        var old = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay), null, ImagePng: BigImage(2)));
        await Begin(relay, old);
        await Chunk(relay, old.Chunks[0]);

        await relay.FirePeersChanged();
        await TransitionTestHelper.BringRemoteOnline(relay);
        await Chunks(relay, old, from: 1);
        await service.Flush();
        await service.HashReads();
        var afterOld = new { clipboard.Text, clipboard.ImagePng, clipboard.SetClipboardCallCount };
        VisitAndLeaveRemote(platform);
        var image = BigImage(3);
        var fresh = StreamedAnswer(7, new ClipboardPullResponseMessage(PullIdTo(relay), null, ImagePng: image));
        await Begin(relay, fresh);
        await Chunks(relay, fresh);
        await service.Flush();
        await service.HashReads();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterOld.Text, Is.Null);
            Assert.That(afterOld.ImagePng, Is.Null);
            Assert.That(afterOld.SetClipboardCallCount, Is.Zero);
            Assert.That(clipboard.ImagePng, Is.EqualTo(image));
        }
    }

    // -- 8. the slave: the master leaves mid-stream to it, while its own answer streams out --

    [Test]
    public async Task Slave_MasterLeavesMidStreamBothWays_NothingApplies_NoOsdStorm_AndItsOwnStreamStops()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupImage(BigImage(1));
        var osd = new RecordingOsd();
        var clock = new FakeTimeProvider();
        var slave = Slave(clipboard, osd, clock);
        slave.Reliable.Hold();
        await Pull(slave);
        await slave.Sent.WaitFor(MessageKind.ClipboardStreamBegin, 1, Master);
        var outgoing = slave.Sent.Of<ClipboardStreamBeginMessage>(MessageKind.ClipboardStreamBegin, Master).Single().Id;
        var push = HashedPush(5, new ClipboardPushMessage(BigText('a')));
        await Begin(slave, push);
        await Chunk(slave, push.Chunks[0]);

        await slave.Peers([]);
        await Chunks(slave, push, from: 1);
        clock.Advance(ClipboardStreamReceiver.Watchdog);
        var afterDeparture = new { clipboard.Text, clipboard.ImagePng, clipboard.SetClipboardCallCount };
        var shown = await Shown(osd);
        slave.Reliable.Release();
        // a fresh answer runs start to end on the same loop, so the dropped stream's step has long passed
        var image = BigImage(2);
        clipboard.SetupImage(image);
        await Pull(slave, 2);
        var answer = await SentAnswer(slave, begins: 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterDeparture.Text, Is.Null);
            Assert.That(afterDeparture.ImagePng, Is.Null);
            Assert.That(afterDeparture.SetClipboardCallCount, Is.Zero);
            Assert.That(shown, Has.Count.LessThanOrEqualTo(1));
            Assert.That(shown, Is.All.EqualTo(ClipboardStreamReceiver.FailedOsd));
            Assert.That(answer.ImagePng, Is.EqualTo(image));
            Assert.That(slave.Sent.Of<ClipboardStreamChunkMessage>(MessageKind.ClipboardStreamChunk).Select(c => c.Id), Has.None.EqualTo(outgoing));
        }
    }

    // -- 9. the slave: the relay disconnects mid-stream --

    [Test]
    public async Task Slave_RelayDisconnectsWhileItWaitsOnAPush_ItStaysHonestlyEmpty_AndIsToldOnce()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the slave");
        var osd = new RecordingOsd();
        var clock = new FakeTimeProvider();
        var slave = Slave(clipboard, osd, clock);
        await slave.SimulateReceive(Master, MessageKind.EnterScreen, new EnterScreenMessage("slave", 5, 5, 100, 100).ToSaneJson());
        var push = HashedPush(5, new ClipboardPushMessage(BigText('a')));
        await Begin(slave, push);
        await Chunk(slave, push.Chunks[0]);

        await slave.SimulateDisconnected();
        await Chunks(slave, push, from: 1);
        clock.Advance(ClipboardStreamReceiver.Watchdog);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(clipboard.SetClipboardCallCount, Is.Zero);
            Assert.That(clipboard.Text, Is.Null);
            Assert.That(await Shown(osd), Is.EqualTo([ClipboardStreamReceiver.FailedOsd]));
        }
    }

    [Test]
    public async Task Slave_RelayDisconnectsMidStreamBothWays_BothStop_AndFreshStreamsRunEndToEndIgnoringALateOldChunk()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupImage(BigImage(1));
        var slave = Slave(clipboard);
        slave.Reliable.Hold();
        await Pull(slave);
        await slave.Sent.WaitFor(MessageKind.ClipboardStreamBegin, 1, Master);
        var outgoing = slave.Sent.Of<ClipboardStreamBeginMessage>(MessageKind.ClipboardStreamBegin, Master).Single().Id;
        var old = HashedPush(5, new ClipboardPushMessage(BigText('a')));
        await Begin(slave, old);
        await Chunk(slave, old.Chunks[0]);

        await slave.SimulateDisconnected();
        await Chunk(slave, old.Chunks[1]);
        var whileDown = new { clipboard.Text, clipboard.ImagePng, clipboard.SetClipboardCallCount };
        slave.Reliable.Release();
        await slave.SimulateConnected();
        var text = BigText('b');
        var fresh = HashedPush(6, new ClipboardPushMessage(text));
        await Begin(slave, fresh);
        await Chunk(slave, fresh.Chunks[0]);
        await Chunk(slave, old.Chunks[2]);
        await Chunks(slave, fresh, from: 1);
        await Pull(slave, 2);
        var answer = await SentAnswer(slave, begins: 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(whileDown.Text, Is.Null);
            Assert.That(whileDown.ImagePng, Is.Null);
            Assert.That(whileDown.SetClipboardCallCount, Is.Zero);
            Assert.That(clipboard.Text, Is.EqualTo(text));
            Assert.That(answer.Text, Is.EqualTo(text));
            Assert.That(slave.Sent.Of<ClipboardStreamChunkMessage>(MessageKind.ClipboardStreamChunk).Select(c => c.Id), Has.None.EqualTo(outgoing));
        }
    }

    [Test]
    public async Task Slave_RelayDisconnectsMidAnswerOnly_ItsStreamStops_ItKeepsItsClipboard_AndShowsNothing()
    {
        var clipboard = new FakeClipboardSync();
        var image = BigImage(1);
        clipboard.SetupImage(image);
        var osd = new RecordingOsd();
        var slave = Slave(clipboard, osd);
        slave.Reliable.Hold();
        await Pull(slave);
        await slave.Sent.WaitFor(MessageKind.ClipboardStreamBegin, 1, Master);
        var outgoing = slave.Sent.Of<ClipboardStreamBeginMessage>(MessageKind.ClipboardStreamBegin, Master).Single().Id;

        await slave.SimulateDisconnected();
        slave.Reliable.Release();
        await Pull(slave, 2);
        var answer = await SentAnswer(slave, begins: 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(clipboard.ImagePng, Is.EqualTo(image));
            Assert.That(clipboard.ClearClipboardCallCount, Is.Zero);
            Assert.That(osd.Shown.Count, Is.Zero);
            Assert.That(answer.ImagePng, Is.EqualTo(image));
            Assert.That(slave.Sent.Of<ClipboardStreamChunkMessage>(MessageKind.ClipboardStreamChunk).Select(c => c.Id), Has.None.EqualTo(outgoing));
        }
    }

    // -- 10. the slave: its hash emptied the clipboard for a push, and the master goes before any of it arrives --

    public enum Loss { Disconnect, Departure, Dormancy }

    [TestCase(Loss.Disconnect)]
    [TestCase(Loss.Departure)]
    [TestCase(Loss.Dormancy)]
    public async Task Slave_MasterGoesWhileItAwaitsAPushNoneOfWhichArrived_ItWarnsAndIsToldOnce_SilentlyWhenDormant(Loss loss)
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the slave");
        var osd = new RecordingOsd();
        var log = new CapturingLog<RelayConnection>(LogLevel.Warning);
        var slave = Slave(clipboard, osd, log: log);
        await slave.SimulateReceive(Master, MessageKind.EnterScreen, new EnterScreenMessage("slave", 5, 5, 100, 100).ToSaneJson());
        await slave.SimulateReceive(Master, MessageKind.ClipboardHash, new ClipboardHashMessage(TextHash("on the master"), Visit: 1).ToSaneJson());
        var asked = slave.Sent.Of(MessageKind.ClipboardPullRequest, Master).Count;

        if (loss == Loss.Disconnect) await slave.SimulateDisconnected();
        else if (loss == Loss.Departure) await slave.Peers([]);
        else await slave.Dormancy.Enter();
        // a second loss finds nothing awaited any more
        await slave.SimulateDisconnected();

        var warned = log.Entries.Count(e => e.Message.StartsWith($"Clipboard push from {Master} will not come", StringComparison.Ordinal));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(asked, Is.EqualTo(1), "pre-condition: the hash asked for a push");
            Assert.That(clipboard.Text, Is.Null, "honest empty, never the old clipboard");
            Assert.That(warned, Is.EqualTo(1), "warned once");
            Assert.That(await Shown(osd), loss == Loss.Dormancy ? Is.Empty : Is.EqualTo([ClipboardStreamReceiver.FailedOsd]));
        }
    }

    // another master leaving forgets nothing awaited from this one
    [Test]
    public async Task Slave_AnotherMasterLeavesWhileItAwaitsAPush_ItStaysAwaited()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the slave");
        var osd = new RecordingOsd();
        var slave = Slave(clipboard, osd);
        await slave.SimulateReceive(Master, MessageKind.ClipboardHash, new ClipboardHashMessage(TextHash("on the master"), Visit: 1).ToSaneJson());

        await slave.Peers([Master]);
        await slave.SimulateReceive(Master, MessageKind.ClipboardPushNone, new ClipboardPushNoneMessage(1).ToSaneJson());

        Assert.That(await Shown(osd), Is.EqualTo([ClipboardStreamReceiver.FailedOsd]), "still awaited: its PushNone is the one told");
    }

    // a dropped stream and the push it carried are one failure, shown once
    [Test]
    public async Task Slave_RelayDisconnectsMidPushStream_IsToldOnce()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the slave");
        var osd = new RecordingOsd();
        var slave = Slave(clipboard, osd);
        await slave.SimulateReceive(Master, MessageKind.ClipboardHash, new ClipboardHashMessage(TextHash("on the master"), Visit: 1).ToSaneJson());
        var push = HashedPush(5, new ClipboardPushMessage(BigText('a')));
        await Begin(slave, push);
        await Chunk(slave, push.Chunks[0]);

        await slave.SimulateDisconnected();

        Assert.That(await Shown(osd), Is.EqualTo([ClipboardStreamReceiver.FailedOsd]));
    }

    // -- helpers: the master --

    // home → remote, online
    private Task<TestServiceBundle> StartSingle(FakeClipboardSync clipboard, RecordingOsd? osd = null, FakeTimeProvider? clock = null) =>
        Services.StartOnline(TransitionTestHelper.CreateService(clipboard: clipboard, osd: osd, timeProvider: clock));

    private static List<string> OsdsTo(FakeRelay relay, string host) => [.. relay.Sent.Of<OsdMessage>(MessageKind.Osd, host).Select(o => o.Text)];

    // -- helpers: a slave --

    private TestableSlaveRelay Slave(FakeClipboardSync clipboard, RecordingOsd? osd = null, FakeTimeProvider? clock = null,
        ILogger<RelayConnection>? log = null) =>
        _slaves.New(clipboard, osd ?? new RecordingOsd(), clock ?? new FakeTimeProvider(), log);
}
