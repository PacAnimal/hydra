using Hydra.Config;
using Hydra.Platform;
using Hydra.Relay;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.Relay;

[TestFixture]
public class ActivityTrackerTests
{
    private static readonly IHydraProfile SlaveProfile =
        TransitionTestHelper.Profile("slave", new HydraConfig { Mode = Mode.Slave });

    private static IHydraProfile MasterProfile(bool sync = true) =>
        TransitionTestHelper.Profile("master", new HydraConfig { Mode = Mode.Master, SyncScreensaver = sync });

    private sealed class SpySender : IRelaySender
    {
        public readonly List<(string[] Targets, MessageKind Kind)> Sent = [];
        public bool IsConnected => true;
        public void Send(string[] targets, byte[] payload) =>
            Sent.Add((targets, MessageSerializer.Decode(payload).Kind));
#pragma warning disable CS0067
        public event Func<string[], Task>? PeersChanged;
        public event Func<string, MessageKind, ReadOnlyMemory<byte>, Task>? MessageReceived;
        public event Func<Task>? Disconnected;
#pragma warning restore CS0067
    }

    private static ActivityTracker Make(IHydraProfile profile, SpySender relay, IWorldState world, Func<long>? clock = null) =>
        new(profile, new Lazy<IRelaySender>(() => relay), world, new NullScreenSaverSync(), NullLogger<ActivityTracker>.Instance, clock);

    [Test]
    public async Task LocalActivity_SlaveMode_BroadcastsToMaster()
    {
        var spy = new SpySender();
        var world = new WorldState();
        await world.AddMaster("master-pc", new MasterConfigMessage(null));
        var tracker = Make(SlaveProfile, spy, world);

        await tracker.LocalActivity();

        Assert.That(spy.Sent, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(spy.Sent[0].Targets, Is.EqualTo(["master-pc"]));
            Assert.That(spy.Sent[0].Kind, Is.EqualTo(MessageKind.ActivityPing));
        }
    }

    [Test]
    public async Task LocalActivity_MasterWithSyncOn_BroadcastsToSlaves()
    {
        var spy = new SpySender();
        var world = new WorldState();
        await world.SetPeerScreens("slave-pc", []);
        var tracker = Make(MasterProfile(sync: true), spy, world);

        await tracker.LocalActivity();

        Assert.That(spy.Sent, Has.Count.EqualTo(1));
        Assert.That(spy.Sent[0].Targets, Is.EqualTo(["slave-pc"]));
    }

    [Test]
    public async Task LocalActivity_MasterWithSyncOff_DoesNotBroadcast()
    {
        var spy = new SpySender();
        var world = new WorldState();
        await world.SetPeerScreens("slave-pc", []);
        var tracker = Make(MasterProfile(sync: false), spy, world);

        await tracker.LocalActivity();

        Assert.That(spy.Sent, Is.Empty);
    }

    [Test]
    public async Task LocalActivity_Throttled_SecondCallSkipped()
    {
        var spy = new SpySender();
        var world = new WorldState();
        await world.AddMaster("master-pc", new MasterConfigMessage(null));
        var clock = 10_000L;
        var tracker = Make(SlaveProfile, spy, world, () => clock);

        await tracker.LocalActivity();
        await tracker.LocalActivity();

        Assert.That(spy.Sent, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task LocalActivity_AfterThrottleWindow_BroadcastsAgain()
    {
        var spy = new SpySender();
        var world = new WorldState();
        await world.AddMaster("master-pc", new MasterConfigMessage(null));
        var clock = new[] { 10_000L };
        var tracker = Make(SlaveProfile, spy, world, () => clock[0]);

        await tracker.LocalActivity();
        clock[0] += 6_000;
        await tracker.LocalActivity();

        Assert.That(spy.Sent, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task RemoteActivity_SyncOff_DoesNotBroadcast()
    {
        var spy = new SpySender();
        var world = new WorldState();
        await world.SetPeerScreens("slave1", []);
        await world.SetPeerScreens("slave2", []);
        var tracker = Make(MasterProfile(sync: false), spy, world);

        await tracker.RemoteActivity("slave1");

        Assert.That(spy.Sent, Is.Empty);
    }

    [Test]
    public async Task RemoteActivity_SyncOn_BroadcastsExcludingSource()
    {
        var spy = new SpySender();
        var world = new WorldState();
        await world.SetPeerScreens("slave1", []);
        await world.SetPeerScreens("slave2", []);
        var tracker = Make(MasterProfile(sync: true), spy, world);

        await tracker.RemoteActivity("slave1");

        Assert.That(spy.Sent, Has.Count.EqualTo(1));
        Assert.That(spy.Sent[0].Targets, Is.EqualTo(["slave2"]));
    }

    [Test]
    public async Task RemoteActivity_SyncOn_ResetsLocalIdleTimer()
    {
        var sync = new FakeScreenSaverSync();
        var world = new WorldState();
        await world.SetPeerScreens("slave1", []);
        var spy = new SpySender();
        var tracker = new ActivityTracker(MasterProfile(sync: true), new Lazy<IRelaySender>(() => spy), world, sync,
            NullLogger<ActivityTracker>.Instance);

        await tracker.RemoteActivity("slave1");

        Assert.That(sync.ResetIdleTimerCalled, Is.True);
    }

    [Test]
    public async Task LocalActivity_ResetsLocalIdleTimer()
    {
        var sync = new FakeScreenSaverSync();
        var world = new WorldState();
        await world.AddMaster("master-pc", new MasterConfigMessage(null));
        var spy = new SpySender();
        var tracker = new ActivityTracker(SlaveProfile, new Lazy<IRelaySender>(() => spy), world, sync,
            NullLogger<ActivityTracker>.Instance);

        await tracker.LocalActivity();

        Assert.That(sync.ResetIdleTimerCalled, Is.True);
    }

    [Test]
    public async Task IncomingPing_ResetsLocalIdleTimer()
    {
        var sync = new FakeScreenSaverSync();
        var world = new WorldState();
        var spy = new SpySender();
        var tracker = new ActivityTracker(SlaveProfile, new Lazy<IRelaySender>(() => spy), world, sync,
            NullLogger<ActivityTracker>.Instance);

        await tracker.IncomingPing();

        Assert.That(sync.ResetIdleTimerCalled, Is.True);
    }

    [Test]
    public async Task IncomingPing_DoesNotUpdateLocalActivityTick()
    {
        var clock = new[] { 10_000L };
        var spy = new SpySender();
        var world = new WorldState();
        var tracker = Make(SlaveProfile, spy, world, () => clock[0]);
        // baseline: MsSinceLocalActivity is huge (no activity recorded)
        var before = tracker.MsSinceLocalActivity;

        await tracker.IncomingPing();

        // clock advanced — MsSinceLocalActivity should still grow, not reset
        clock[0] += 1_000;
        Assert.That(tracker.MsSinceLocalActivity, Is.GreaterThan(before));
    }
}
