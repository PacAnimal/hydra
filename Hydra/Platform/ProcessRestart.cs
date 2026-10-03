using System.Diagnostics;
using System.Runtime.InteropServices;
using Cathedral.Utils;

namespace Hydra.Platform;

internal static partial class ProcessRestart
{
    private static readonly Toggle Restarting = new(); // one-shot latch — restart already initiated

    internal static void Restart()
    {
        // one restart only — a racing caller (NetworkWatcher + SelfUpdater, or an event burst) must not
        // spawn a second process (Windows) before Environment.Exit runs
        if (!Restarting.TrySet()) return;

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

    [LibraryImport("libc", EntryPoint = "execv", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Execv(string pathname, string?[] argv);
}
