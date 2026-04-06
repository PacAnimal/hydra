using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Hydra.Platform;

internal static partial class ProcessRestart
{
    internal static void Restart()
    {
        var exePath = Environment.ProcessPath!;

        if (OperatingSystem.IsWindows())
        {
            // windows has no exec() — start a new process and exit
            var info = new ProcessStartInfo { FileName = exePath, UseShellExecute = false };
            foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
                info.ArgumentList.Add(arg);
            Process.Start(info);
            Environment.Exit(0);
        }
        else
        {
            // exec() replaces the process image in-place — same PID, same process group, terminal grip preserved
            var args = Environment.GetCommandLineArgs();
            var argv = new string?[args.Length + 1]; // null-terminated
            Array.Copy(args, argv, args.Length);
            execv(exePath, argv);
            Environment.Exit(1); // execv only returns on failure
        }
    }

    [LibraryImport("libc", EntryPoint = "execv", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int execv(string pathname, string?[] argv);
}
