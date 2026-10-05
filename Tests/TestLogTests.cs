using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tests.Setup;

namespace Tests;

/// <summary>Two test runs at once each keep their own log, and neither prunes the other's.</summary>
[TestFixture]
public class TestLogTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp() => _dir = TestPaths.FreshFixtureRoot(nameof(TestLogTests));

    [Test]
    public void PruningSparesALogWhoseProcessIsStillRunning()
    {
        var now = DateTime.UtcNow;
        var logs = Enumerable.Range(0, TestLog.KeepRuns + 2)
            .Select(age => Log(age == TestLog.KeepRuns + 1 ? TestLog.LogFileName(now, TestLog.Machine, Environment.ProcessId) : $"{age}.log", now.AddMinutes(-age)))
            .ToArray();

        TestLog.PruneOldRuns(_dir);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.Exists(logs[^1]), Is.True, "the oldest log belongs to a run that is still writing it");
            Assert.That(File.Exists(logs[^2]), Is.False, "a finished run past the ceiling should have gone");
            Assert.That(logs[..TestLog.KeepRuns].All(File.Exists), Is.True);
        }
    }

    // the Linux container's pids are from its own namespace, so one that happens to be live here proves nothing
    [Test]
    public void PruningIgnoresWhetherAnotherMachinesPidIsRunningHere()
    {
        var now = DateTime.UtcNow;
        var age = TestLog.ForeignLogGrace + TimeSpan.FromMinutes(TestLog.KeepRuns + 1);
        var foreign = Log(TestLog.LogFileName(now, "container", Environment.ProcessId), now - age);
        Fill(now);

        TestLog.PruneOldRuns(_dir);

        Assert.That(File.Exists(foreign), Is.False, "a pid from another machine kept its long-finished log alive");
    }

    [Test]
    public void PruningSparesAnotherMachinesRecentLog()
    {
        var now = DateTime.UtcNow;
        var foreign = Log(TestLog.LogFileName(now, "container", int.MaxValue), now - TimeSpan.FromSeconds(TestLog.KeepRuns + 1));
        var finished = Log(TestLog.LogFileName(now, TestLog.Machine, int.MaxValue), now - TimeSpan.FromSeconds(TestLog.KeepRuns + 1) + TimeSpan.FromMilliseconds(1));
        Fill(now);

        TestLog.PruneOldRuns(_dir);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.Exists(foreign), Is.True, "another machine's run written to just now may still be going");
            Assert.That(File.Exists(finished), Is.False, "this machine's run past the ceiling has no process left");
        }
    }

    // hosts opening the file themselves would contend for it, and windows refuses the loser's write
    [Test]
    public void EveryHostWritesThroughTheOneFileProvider()
    {
        using var first = Host();
        using var second = Host();

        Assert.That(first.GetServices<ILoggerProvider>().Single(), Is.SameAs(second.GetServices<ILoggerProvider>().Single()));

        static ServiceProvider Host()
        {
            var services = new ServiceCollection();
            TestLog.ConfigureFileLogging(services);
            return services.BuildServiceProvider();
        }
    }

    // windows answers HasExited for a protected process with access denied
    [Test]
    public void PruningPresumesAProcessItCannotAskAboutIsRunning() =>
        Assert.That(LiveProcess.HasNotExited(Environment.ProcessId, _ => throw new Win32Exception(5)), Is.True);

    // a full history of this machine's finished runs, newest first and a second apart
    private void Fill(DateTime now)
    {
        for (var age = 0; age < TestLog.KeepRuns; age++)
            Log(TestLog.LogFileName(now, TestLog.Machine, int.MaxValue - 1 - age), now - TimeSpan.FromSeconds(age));
    }

    private string Log(string name, DateTime written)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "log");
        File.SetLastWriteTimeUtc(path, written);
        return path;
    }
}
