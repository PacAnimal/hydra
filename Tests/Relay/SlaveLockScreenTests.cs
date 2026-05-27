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
    public async Task LockScreen_SlaveIdleEqualToMasterGap_Locks()
    {
        var (relay, sync) = await Setup();
        // slave has been idle exactly as long as master — no local activity
        sync.IdleTime = TimeSpan.FromSeconds(30);
        await SendLock(relay, 30_000);
        Assert.That(sync.LockScreenCalled, Is.True);
    }

    [Test]
    public async Task LockScreen_SlaveIdleLongerThanMasterGap_Locks()
    {
        var (relay, sync) = await Setup();
        // slave has been idle longer — master had more recent input (e.g. was active on another slave)
        sync.IdleTime = TimeSpan.FromSeconds(60);
        await SendLock(relay, 30_000);
        Assert.That(sync.LockScreenCalled, Is.True);
    }

    [Test]
    public async Task LockScreen_SlaveIdleShorterThanMasterGap_SkipsLock()
    {
        var (relay, sync) = await Setup();
        // slave had local input more recently than master's last input — user is at the slave
        sync.IdleTime = TimeSpan.FromSeconds(5);
        await SendLock(relay, 30_000);
        Assert.That(sync.LockScreenCalled, Is.False);
    }

    [Test]
    public async Task LockScreen_IdleTimeUnavailable_Locks()
    {
        var (relay, sync) = await Setup();
        // platform doesn't support idle detection — lock unconditionally
        sync.IdleTime = null;
        await SendLock(relay, 30_000);
        Assert.That(sync.LockScreenCalled, Is.True);
    }

}
