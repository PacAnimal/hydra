using System.Diagnostics;
using System.Runtime.Versioning;
using Hydra.Platform.MacOs;
using Tests.Setup;

namespace Tests.Platform;

[TestFixture]
public class OsaScriptTests
{
    private string _root = null!;

    [OneTimeSetUp]
    public void RequireMacOs()
    {
        if (!OperatingSystem.IsMacOS()) Assert.Ignore("macOS-only: osascript");
    }

    [SetUp]
    public void SetUp() => _root = TestPaths.FreshFixtureRoot(nameof(OsaScriptTests));

    [Test]
    [SupportedOSPlatform("macos")]
    public void Run_ReturnsTheScriptsOutput()
    {
        var result = OsaScript.Run("return \"hydra\"", TimeSpan.FromSeconds(30));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.True, result.Stderr);
            Assert.That(result.TimedOut, Is.False);
            Assert.That(result.Stdout.Trim(), Is.EqualTo("hydra"));
        }
    }

    [Test]
    [SupportedOSPlatform("macos")]
    public void Run_KillsAScriptThatOutlivesItsTimeout()
    {
        var stopwatch = Stopwatch.StartNew();

        var result = OsaScript.Run("delay 60", TimeSpan.FromMilliseconds(500));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.TimedOut, Is.True);
            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(30)), "the script was waited out rather than killed");
        }
    }

    [Test]
    [SupportedOSPlatform("macos")]
    public void Run_DoesNotWaitOnADescendantStillHoldingTheOutputAfterTheKill()
    {
        // the backgrounded sleep is orphaned, so the tree kill misses it and it keeps the stdout pipe open
        var pidFile = Path.Combine(_root, "orphan.pid");
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = OsaScript.Run("/bin/sh", ["-c", $"(sleep 60 & echo $! > '{pidFile}'); sleep 60"], TimeSpan.FromMilliseconds(500));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.Success, Is.False);
                Assert.That(result.TimedOut, Is.True);
                Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(30)), "the read waited on the surviving descendant");
            }
        }
        finally
        {
            KillOrphan(pidFile);
        }
    }

    [Test]
    [SupportedOSPlatform("macos")]
    public void Run_DoesNotWaitOnADescendantStillHoldingTheOutputAfterANormalExit()
    {
        var pidFile = Path.Combine(_root, "orphan.pid");
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = OsaScript.Run("/bin/sh", ["-c", $"(sleep 60 & echo $! > '{pidFile}'); echo hydra"], TimeSpan.FromSeconds(30));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.Success, Is.False, "output still open is output not fully read");
                Assert.That(result.TimedOut, Is.False);
                Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(30)), "the read waited on the surviving descendant");
            }
        }
        finally
        {
            KillOrphan(pidFile);
        }
    }

    [Test]
    [SupportedOSPlatform("macos")]
    public async Task Run_KillsTheScriptWhenCancelled()
    {
        var fifo = Path.Combine(_root, "script.fifo");
        using (var mkfifo = Process.Start("/usr/bin/mkfifo", [fifo]))
            await mkfifo.WaitForExitAsync();
        using var cancel = new CancellationTokenSource();
        var token = cancel.Token;
        var run = Task.Run(() => OsaScript.Run("/bin/sh", ["-c", $"echo $$ > '{fifo}'; exec sleep 60"], TimeSpan.FromSeconds(60), token));
        // opening a fifo blocks until the other end does, so the script is running once this returns
        var pid = int.Parse(await Task.Run(() => File.ReadAllText(fifo)).WaitAsync(Bound));

        await cancel.CancelAsync();

        await Assert.CatchAsync<OperationCanceledException>(() => run.WaitAsync(Bound), "the script ran on past the cancel");
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid).Dispose(), "the script outlived the cancel");
    }

    [Test]
    [SupportedOSPlatform("macos")]
    public void Run_StartsNothingWhenAlreadyCancelled()
    {
        Assert.Catch<OperationCanceledException>(() => OsaScript.Run("return 1", TimeSpan.FromSeconds(30), new CancellationToken(canceled: true)));
    }

    private static void KillOrphan(string pidFile)
    {
        if (!File.Exists(pidFile)) return;
        if (int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid))
        {
            try { Process.GetProcessById(pid).Kill(); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { } // already gone
        }
    }
}
