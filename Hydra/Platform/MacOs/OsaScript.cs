using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.MacOs;

[SupportedOSPlatform("macos")]
internal static class OsaScript
{
    // Finder walks a large selection slowly, and the first-run Automation consent prompt blocks until answered
    internal static readonly TimeSpan FinderTimeout = TimeSpan.FromSeconds(30);

    // a killed script exits at once, and an exited one's pipes close with it, so this only bounds the wait on
    // a script or descendant that will not let go
    private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(1);

    public readonly record struct Result(bool Success, string Stdout, string Stderr, int ExitCode, bool TimedOut = false);

    // a script still running at the timeout is killed and reported as a timed-out failure; one still running when
    // cancelled is killed and throws OperationCanceledException
    public static Result Run(string script, TimeSpan timeout, CancellationToken cancel = default) =>
        Run("/usr/bin/osascript", ["-e", script], timeout, cancel);

    // any command under the same timeout and kill, for a test that needs a process osascript cannot produce
    internal static Result Run(string fileName, string[] arguments, TimeSpan timeout, CancellationToken cancel = default)
    {
        cancel.ThrowIfCancellationRequested();
        using var proc = Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (proc == null) return new Result(false, string.Empty, string.Empty, -1);

        var stdout = proc.StandardOutput.ReadToEndAsync(cancel);
        var stderr = proc.StandardError.ReadToEndAsync(cancel);
        bool exited;
        // a killed script exits, which ends the wait
        using (cancel.Register(static state => Kill((Process)state!), proc))
            exited = proc.WaitForExit(timeout);
        cancel.ThrowIfCancellationRequested();
        if (!exited)
        {
            Kill(proc);
            // a script that survived the kill, or a descendant still holding its pipes, is abandoned rather than waited on
            if (!proc.WaitForExit(KillGrace)) return new Result(false, string.Empty, string.Empty, -1, TimedOut: true);
            _ = Task.WaitAll([stdout, stderr], KillGrace);
            return new Result(false, ReadSoFar(stdout), ReadSoFar(stderr), -1, TimedOut: true);
        }
        // a descendant still holding the pipes means the output is incomplete, so that is a failure too
        var drained = Task.WaitAll([stdout, stderr], KillGrace);
        return new Result(proc.ExitCode == 0 && drained, ReadSoFar(stdout), ReadSoFar(stderr), proc.ExitCode);
    }

    public static void LogFailure(this Result result, ILogger log, LogLevel level)
    {
        if (result.TimedOut) log.LogWarning("osascript timed out and was killed");
        else if (result.ExitCode == 0) log.Log(level, "osascript exited but something it started kept its output open");
        else if (log.IsEnabled(level)) log.Log(level, "osascript exited {Code}: {Stderr}", result.ExitCode, result.Stderr.Trim());
    }

    private static string ReadSoFar(Task<string> read) => read.IsCompletedSuccessfully ? read.Result : string.Empty;

    private static void Kill(Process proc)
    {
        try { proc.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { } // exited meanwhile
        catch (Exception ex) when (ex is Win32Exception or AggregateException)
        {
            // the tree walk failed; still take the script itself down
            try { proc.Kill(); }
            catch (Exception inner) when (inner is InvalidOperationException or Win32Exception) { }
        }
    }
}
