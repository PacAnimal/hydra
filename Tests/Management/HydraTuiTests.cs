using Hydra.Management;

namespace Tests.Management;

public class HydraTuiTests
{
    [Test]
    public void RestartCompletionDetectsWindowsProcessReplacement()
    {
        var previous = Status(processId: 10, uptime: 120);
        var current = Status(processId: 11, uptime: 1);

        Assert.That(global::Hydra.HydraTui.HasRestarted(previous, current), Is.True);
    }

    [Test]
    public void RestartCompletionDetectsUnixExecByResetUptime()
    {
        var previous = Status(processId: 10, uptime: 120);
        var current = Status(processId: 10, uptime: 1);

        Assert.That(global::Hydra.HydraTui.HasRestarted(previous, current), Is.True);
    }

    [Test]
    public void RestartCompletionRejectsOrdinaryStatusRefresh()
    {
        var previous = Status(processId: 10, uptime: 120);
        var current = Status(processId: 10, uptime: 121);

        Assert.That(global::Hydra.HydraTui.HasRestarted(previous, current), Is.False);
    }

    [Test]
    public void RelayReconnectCompletionRequiresANewerConnectionAttempt()
    {
        var previous = Status(processId: 10, uptime: 120, relayAttempts: 3);
        var oldConnection = Status(processId: 10, uptime: 121, relayAttempts: 3);
        var newConnection = Status(processId: 10, uptime: 122, relayAttempts: 4);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(global::Hydra.HydraTui.HasRelayReconnected(previous, oldConnection), Is.False);
            Assert.That(global::Hydra.HydraTui.HasRelayReconnected(previous, newConnection), Is.True);
        }
    }

    private static HydraStatusSnapshot Status(int processId, long uptime, long? relayAttempts = null) => new(
        DateTimeOffset.UtcNow, "0.0.0", processId, uptime, "config", "revision", "host", "profile",
        Hydra.Config.Mode.Master, false, relayAttempts != null,
        relayAttempts == null ? null : new RelayConnectionStatus(
            "en0", "Ethernet", "127.0.0.1", 50000, "relay", "127.0.0.1", 51600,
            DateTimeOffset.UtcNow, relayAttempts.Value, 0, 0, 0, 0),
        [], [], [], false, [], [], null);
}
