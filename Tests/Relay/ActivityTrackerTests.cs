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

    private static IHydraProfile MasterProfile(bool sync = true, bool allowSystemSleep = false) =>
        TransitionTestHelper.Profile("master", new HydraConfig
        {
            Mode = Mode.Master,
            SyncScreensaver = sync,
            AllowSystemSleep = allowSystemSleep
        });

    private static IHydraProfile SleepableSlaveProfile =>
        TransitionTestHelper.Profile("slave", new HydraConfig { Mode = Mode.Slave, AllowSystemSleep = true });

    private static ActivityTracker Make(IHydraProfile profile, FakeRelay relay, IWorldState world, Func<long>? clock = null) =>
        new(profile, new Lazy<IRelaySender>(() => relay), world, new NullScreenSaverSync(), NullLogger<ActivityTracker>.Instance, clock);

    [Test]
    public async Task LocalActivity_SlaveMode_BroadcastsToMaster()
    {
        var spy = new FakeRelay();
        var world = new WorldState();
        await world.AddMaster("master-pc", new MasterConfigMessage(null));
        var tracker = Make(SlaveProfile, spy, world);

        await tracker.LocalActivity();

        Assert.That(spy.Snapshot(), Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(spy.Snapshot()[0].Targets, Is.EqualTo(["master-pc"]));
            Assert.That(spy.Snapshot()[0].Kind, Is.EqualTo(MessageKind.ActivityPing));
        }
    }

    [Test]
    public async Task LocalActivity_MasterWithSyncOn_BroadcastsToSlaves()
    {
        var spy = new FakeRelay();
        var world = new WorldState();
        await world.SetPeerScreens("slave-pc", []);
        var tracker = Make(MasterProfile(sync: true), spy, world);

        await tracker.LocalActivity();

        Assert.That(spy.Snapshot(), Has.Count.EqualTo(1));
        Assert.That(spy.Snapshot()[0].Targets, Is.EqualTo(["slave-pc"]));
    }

    [Test]
    public async Task LocalActivity_MasterWithSyncOff_DoesNotBroadcast()
    {
        var spy = new FakeRelay();
        var world = new WorldState();
        await world.SetPeerScreens("slave-pc", []);
        var tracker = Make(MasterProfile(sync: false), spy, world);

        await tracker.LocalActivity();

        Assert.That(spy.Snapshot(), Is.Empty);
    }

    [Test]
    public async Task LocalActivity_Throttled_SecondCallSkipped()
    {
        var spy = new FakeRelay();
        var world = new WorldState();
        await world.AddMaster("master-pc", new MasterConfigMessage(null));
        var clock = 10_000L;
        var tracker = Make(SlaveProfile, spy, world, () => clock);

        await tracker.LocalActivity();
        await tracker.LocalActivity();

        Assert.That(spy.Snapshot(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task LocalActivity_AfterThrottleWindow_BroadcastsAgain()
    {
        var spy = new FakeRelay();
        var world = new WorldState();
        await world.AddMaster("master-pc", new MasterConfigMessage(null));
        var clock = new[] { 10_000L };
        var tracker = Make(SlaveProfile, spy, world, () => clock[0]);

        await tracker.LocalActivity();
        clock[0] += 6_000;
        await tracker.LocalActivity();

        Assert.That(spy.Snapshot(), Has.Count.EqualTo(2));
    }

    [Test]
    public async Task RemoteActivity_SyncOff_DoesNotBroadcast()
    {
        var spy = new FakeRelay();
        var world = new WorldState();
        await world.SetPeerScreens("slave1", []);
        await world.SetPeerScreens("slave2", []);
        var tracker = Make(MasterProfile(sync: false), spy, world);

        await tracker.RemoteActivity("slave1");

        Assert.That(spy.Snapshot(), Is.Empty);
    }

    [Test]
    public async Task RemoteActivity_SyncOn_BroadcastsExcludingSource()
    {
        var spy = new FakeRelay();
        var world = new WorldState();
        await world.SetPeerScreens("slave1", []);
        await world.SetPeerScreens("slave2", []);
        var tracker = Make(MasterProfile(sync: true), spy, world);

        await tracker.RemoteActivity("slave1");

        Assert.That(spy.Snapshot(), Has.Count.EqualTo(1));
        Assert.That(spy.Snapshot()[0].Targets, Is.EqualTo(["slave2"]));
    }

    [Test]
    public async Task RemoteActivity_SyncOn_ResetsLocalIdleTimer()
    {
        var sync = new FakeScreenSaverSync();
        var world = new WorldState();
        await world.SetPeerScreens("slave1", []);
        var spy = new FakeRelay();
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
        var spy = new FakeRelay();
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
        var spy = new FakeRelay();
        var tracker = new ActivityTracker(SlaveProfile, new Lazy<IRelaySender>(() => spy), world, sync,
            NullLogger<ActivityTracker>.Instance);

        await tracker.IncomingPing();

        Assert.That(sync.ResetIdleTimerCalled, Is.True);
    }

    [Test]
    public async Task IncomingPing_WhenSystemSleepAllowed_DoesNotResetLocalIdleTimer()
    {
        var sync = new FakeScreenSaverSync();
        var tracker = new ActivityTracker(SleepableSlaveProfile, new Lazy<IRelaySender>(() => new FakeRelay()),
            new WorldState(), sync, NullLogger<ActivityTracker>.Instance);

        await tracker.IncomingPing();

        Assert.That(sync.ResetIdleTimerCalled, Is.False);
    }

    [Test]
    public async Task RemoteActivity_WhenSystemSleepAllowed_DoesNotResetMasterIdleTimer()
    {
        var sync = new FakeScreenSaverSync();
        var tracker = new ActivityTracker(MasterProfile(allowSystemSleep: true),
            new Lazy<IRelaySender>(() => new FakeRelay()), new WorldState(), sync,
            NullLogger<ActivityTracker>.Instance);

        await tracker.RemoteActivity("slave1");

        Assert.That(sync.ResetIdleTimerCalled, Is.False);
    }

    [Test]
    public async Task IncomingPing_DoesNotUpdateLocalActivityTick()
    {
        var clock = new[] { 10_000L };
        var spy = new FakeRelay();
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
