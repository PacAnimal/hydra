using Hydra.Platform;
using Tests.Setup;

namespace Tests.Platform;

[TestFixture]
public class ProcessRestartTests
{
    // the restarting process's own files go, and nothing else does — a neighbour's diagnostic pair shares
    // the directory and an unrelated file shares nothing but the prefix
    [Test]
    public void DropDiagnosticEndpoint_RemovesOnlyThisPidsFiles()
    {
        var dir = TestPaths.FreshFixtureRoot(nameof(ProcessRestartTests));
        const int mine = 4242, theirs = 4243;

        string[] doomed =
        [
            $"dotnet-diagnostic-{mine}-1789800724-socket",
            $"clr-debug-pipe-{mine}-1789800724-in",
            $"clr-debug-pipe-{mine}-1789800724-out"
        ];
        string[] spared =
        [
            $"dotnet-diagnostic-{theirs}-1789800724-socket",
            $"clr-debug-pipe-{theirs}-1789800724-in",
            "hydra.conf"
        ];
        foreach (var name in doomed.Concat(spared))
            File.WriteAllText(Path.Combine(dir, name), "x");

        ProcessRestart.DropDiagnosticEndpoint(dir, mine);

        using (Assert.EnterMultipleScope())
        {
            foreach (var name in doomed)
                Assert.That(File.Exists(Path.Combine(dir, name)), Is.False, $"{name} should have been unlinked");
            foreach (var name in spared)
                Assert.That(File.Exists(Path.Combine(dir, name)), Is.True, $"{name} belongs to someone else");
        }
    }

    [Test]
    public void DropDiagnosticEndpoint_DoesNotThrow_WhenTempDirIsGone()
    {
        var missing = Path.Combine(TestPaths.FreshFixtureRoot(nameof(ProcessRestartTests)), "never-created");
        Assert.DoesNotThrow(() => ProcessRestart.DropDiagnosticEndpoint(missing, 4242));
    }

    // the deadline is a real kernel timer on the test host, so it is armed far out and always cleared — the
    // kill itself is SIGALRM's default action and was verified by hand, not here
    [Test]
    public void SetDeadline_ArmsTheKernelTimer_AndZeroClearsIt()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("Unix interval timer");
        try
        {
            ProcessRestart.SetDeadline(TimeSpan.FromHours(1));
            Assert.That(ProcessRestart.DeadlineRemaining(), Is.InRange(TimeSpan.FromMinutes(59), TimeSpan.FromHours(1)));
        }
        finally
        {
            ProcessRestart.SetDeadline(TimeSpan.Zero);
        }
        Assert.That(ProcessRestart.DeadlineRemaining(), Is.EqualTo(TimeSpan.Zero));
    }
}
