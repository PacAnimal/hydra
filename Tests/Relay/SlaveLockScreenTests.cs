using Hydra.Relay;
using Tests.Setup;

namespace Tests.Relay;

[TestFixture]
public class SlaveLockScreenTests
{
    private static async Task<(TestableSlaveRelay relay, FakeScreenSaverSync sync)> Setup()
    {
        var sync = new FakeScreenSaverSync();
        var relay = new TestableSlaveRelay(screenSaverSync: sync);
        await relay.SimulateConnected();
        await relay.SimulateMasterConfig("master-pc");
        return (relay, sync);
    }

    // sends a LockScreen message with master's ms-since-last-input stamp
    private static Task SendLock(TestableSlaveRelay relay, long masterMsSinceInput) =>
        relay.SimulateReceive("master-pc", MessageKind.LockScreen, $$$"""{"millisecondsSinceLastInput":{{{masterMsSinceInput}}}}""");

    // -- tests --

    [Test]
    public async Task LockScreen_NoLocalActivity_Locks()
    {
        var (relay, sync) = await Setup();
        // slave had no local activity — lock should propagate
        await SendLock(relay, 30_000);
        Assert.That(sync.LockScreenCalled, Is.True);
    }

    [Test]
    public async Task LockScreen_LocalActivityAfterMasterInput_SkipsLock()
    {
        var (relay, sync) = await Setup();
        // slave had local input very recently — user is actively at the slave machine
        await relay.Tracker.LocalActivity();
        await SendLock(relay, 30_000);
        Assert.That(sync.LockScreenCalled, Is.False);
    }

    [Test]
    public async Task LockScreen_LocalActivityBeforeMasterInput_Locks()
    {
        var (relay, sync) = await Setup();
        // no activity recorded — MsSinceLocalActivity will exceed any reasonable gap
        await SendLock(relay, 1_000);
        Assert.That(sync.LockScreenCalled, Is.True);
    }

    [Test]
    public async Task LockScreen_SlaveActivityOlderThanMasterGap_Locks()
    {
        var sync = new FakeScreenSaverSync();
        var clock = new[] { 10_000L };
        var relay = new TestableSlaveRelay(screenSaverSync: sync, trackerClock: () => clock[0]);
        await relay.SimulateConnected();
        await relay.SimulateMasterConfig("master-pc");
        // slave was active 60s ago; master's last input was only 30s ago → slave should lock
        await relay.Tracker.LocalActivity();
        clock[0] += 60_000;
        await SendLock(relay, 30_000);
        Assert.That(sync.LockScreenCalled, Is.True);
    }

    [Test]
    public async Task ActivityPing_ResetsLocalIdleTimer()
    {
        var (relay, sync) = await Setup();
        await relay.SimulateReceive("master-pc", MessageKind.ActivityPing, "{}");
        Assert.That(sync.ResetIdleTimerCalled, Is.True);
    }
}
