using System.Diagnostics;
using System.Runtime.InteropServices;
using Cathedral.Utils;

namespace Hydra.Platform;

internal static partial class ProcessRestart
{
    private static readonly Toggle Restarting = new(); // one-shot latch — restart already initiated

    /// <summary>How long a restart has, from <see cref="Restart"/> to the first line of the new image's Main.</summary>
    /// <remarks>Covers exec, the runtime starting and a first-run bundle extraction on an SD card. A restart
    /// that overruns it is killed and the supervisor starts it fresh, without a deadline — so even a slow
    /// start that trips it costs one extra start, not a loop.</remarks>
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(120);

    internal static void Restart()
    {
        // one restart only — a racing caller (NetworkWatcher + SelfUpdater, or an event burst) must not
        // spawn a second process (Windows) before Environment.Exit runs
        if (!Restarting.TrySet()) return;

        // first, before anything below allocates — see SetDeadline
        SetDeadline(Deadline);

        var exePath = Environment.ProcessPath!;

        if (OperatingSystem.IsWindows())
        {
            // windows has no exec() — start a new process and exit
            var info = new ProcessStartInfo { FileName = exePath, UseShellExecute = false };
            foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
                info.ArgumentList.Add(arg);
            try
            {
                Process.Start(info);
            }
            catch
            {
                Restarting.TryReset(); // spawn failed — don't latch out a later retry
                throw;
            }
            Environment.Exit(0);
        }
        else
        {
            DropDiagnosticEndpoint(Path.GetTempPath(), Environment.ProcessId);

            // exec() replaces the process image in-place — same PID, same process group, terminal grip preserved
            var args = Environment.GetCommandLineArgs();
            var argv = new string?[args.Length + 1]; // null-terminated
            Array.Copy(args, argv, args.Length);
            _ = Execv(exePath, argv);
            Environment.Exit(1); // Execv only returns on failure
        }
    }

    /// <summary>
    /// Unlinks this process's diagnostic IPC files before exec() replaces the image.
    ///
    /// <para>The runtime names them from the PID and the process start time, and exec() preserves both —
    /// so the new image lands on exactly the files this one created and never removed, because exec() runs
    /// no shutdown. Measured: the FIFOs are recreated but the socket's bind fails, so a restarted Hydra has
    /// no endpoint for dotnet-dump / dotnet-counters / dotnet-trace at all, and the stale file is beyond
    /// any PID-based sweeper for as long as the process lives — which is the rest of the session.</para>
    /// </summary>
    internal static void DropDiagnosticEndpoint(string tempDir, int pid)
    {
        var mine = $"-{pid}-";
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(tempDir))
            {
                var name = Path.GetFileName(entry);
                if (!name.Contains(mine, StringComparison.Ordinal)) continue;
                if (!name.StartsWith("dotnet-diagnostic-", StringComparison.Ordinal)
                    && !name.StartsWith("clr-debug-pipe-", StringComparison.Ordinal)) continue;

                try { File.Delete(entry); }
                catch (IOException) { /* the restart matters more than the tidying */ }
                catch (UnauthorizedAccessException) { /* not ours to delete */ }
            }
        }
        catch (IOException) { /* no temp dir to sweep */ }
        catch (UnauthorizedAccessException) { /* temp dir unreadable */ }
    }

    /// <summary>
    /// Arms a kernel timer that kills this process <paramref name="after"/> from now, or clears it when
    /// given zero. Program.cs clears it as its very first statement.
    ///
    /// <para>A restart that wedges must not leave a live-looking process behind. On macOS a slave logged
    /// <c>Update applied, restarting</c> and never exec'd: two threads were re-faulting in native code that
    /// the runtime kept resuming, so they never reached a GC safe point, and the restart's first allocation
    /// waited on that GC for ever. launchd's KeepAlive (and systemd's Restart=) only act on a process that
    /// exits, so it sat at full CPU, off the relay, still running the old binary, until it was killed by hand.</para>
    ///
    /// <para>Nothing managed can rescue a runtime in that state — a watchdog thread waits on the same GC — so
    /// the deadline is the kernel's: <c>ITIMER_REAL</c>, whose SIGALRM terminates by default action (the
    /// runtime installs no handler for it), and which execve preserves. It therefore spans the exec, and
    /// whatever stops the new image reaching Main — a wedged restart, a failed exec, a new binary that hangs
    /// starting — ends with the process dying and the supervisor starting it again. Verified on macOS: a
    /// 3 s timer armed before execv into a .NET app that never clears it kills it at 3 s with status 142.</para>
    ///
    /// <para>Program.cs's startup call is also what compiles this method and its P/Invoke stub, so arming it
    /// at restart time takes no JIT — and the JIT is one of the things that blocks on a pending GC.</para>
    ///
    /// <para>Windows has no interval timers, and restarts by starting a new process rather than exec, so
    /// this is a no-op there.</para>
    /// </summary>
    internal static void SetDeadline(TimeSpan after)
    {
        if (OperatingSystem.IsWindows()) return;
        var timer = new ItimerVal { ValueSec = (long)Math.Ceiling(after.TotalSeconds) };
        _ = SetItimer(ItimerReal, timer, nint.Zero); // best effort — a restart without a deadline is today's behaviour
    }

    /// <summary>Whole seconds left on the deadline; zero when none is armed (and always, on Windows).</summary>
    internal static TimeSpan DeadlineRemaining()
    {
        if (OperatingSystem.IsWindows() || GetItimer(ItimerReal, out var timer) != 0) return TimeSpan.Zero;
        return TimeSpan.FromSeconds(timer.ValueSec); // seconds only — see ItimerVal
    }

    private const int ItimerReal = 0; // same value on Linux and macOS

    // struct itimerval { struct timeval it_interval, it_value; } with a 16-byte timeval on every 64-bit Unix
    // we ship. macOS's tv_usec is 32 bits padded out to 8, so these long fields are exact everywhere as long
    // as usec is written as zero, and read back only for the seconds.
    [StructLayout(LayoutKind.Sequential)]
    private struct ItimerVal
    {
        public long IntervalSec;
        public long IntervalUsec;
        public long ValueSec;
        public long ValueUsec;
    }

    [LibraryImport("libc", EntryPoint = "execv", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Execv(string pathname, string?[] argv);

    [LibraryImport("libc", EntryPoint = "setitimer", SetLastError = true)]
    private static partial int SetItimer(int which, in ItimerVal value, nint oldValue);

    [LibraryImport("libc", EntryPoint = "getitimer", SetLastError = true)]
    private static partial int GetItimer(int which, out ItimerVal value);
}
