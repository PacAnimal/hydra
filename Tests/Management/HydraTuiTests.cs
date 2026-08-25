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

    private static HydraStatusSnapshot Status(int processId, long uptime) => new(
        DateTimeOffset.UtcNow, "0.0.0", processId, uptime, "config", "revision", "host", "profile",
        Hydra.Config.Mode.Master, false, true, null, [], [], false, [], [], null);
}
