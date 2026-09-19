using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Hydra.Platform;

/// <summary>
/// Raises this process above the scheduler's default. Input latency is the whole product: a cursor that
/// stutters on a loaded machine is indistinguishable from a broken link, and the default treatment for a
/// background service — launchd throttles an unclassified agent's CPU and I/O outright — is exactly wrong
/// for a process whose only job is to deliver a keystroke now.
/// </summary>
/// <remarks>
/// Deliberately stops short of the top of the scale. Realtime on Windows, or nice -20, would pre-empt the
/// very threads that deliver our events (WindowServer, Xorg, the kernel's input path), and a spinning
/// realtime process is one the user cannot escape with the mouse — which is the one tool we have taken
/// responsibility for. High / nice -10 clears compilers, VMs, browsers and containers, which is what the
/// machine is actually busy with.
/// </remarks>
internal static partial class ProcessPriority
{
    internal const int UnixNice = -10;
    private const int PrioProcess = 0;

    // EntryPoint spelled out because libc exports these lowercase; without it LibraryImport would look
    // for the C# name and every call would throw EntryPointNotFoundException.
    [LibraryImport("libc", EntryPoint = "setpriority", SetLastError = true)]
    private static partial int SetPriority(int which, uint who, int prio);

    [LibraryImport("libc", EntryPoint = "getpriority", SetLastError = true)]
    private static partial int GetPriority(int which, uint who);

    /// <summary>Raises the current process, returning what it managed, for the startup log.</summary>
    internal static string Raise()
    {
        try
        {
            return OperatingSystem.IsWindows() ? RaiseWindows() : RaiseUnix();
        }
        catch (Exception ex)
        {
            // never let a scheduling tweak be the reason the KVM doesn't come up
            return $"unchanged ({ex.GetType().Name}: {ex.Message})";
        }
    }

    [SupportedOSPlatform("windows")]
    private static string RaiseWindows()
    {
        using var self = Process.GetCurrentProcess();
        // High needs no privilege; the service (LocalSystem) and its session child both reach it
        self.PriorityClass = ProcessPriorityClass.High;
        return "High";
    }

    private static string RaiseUnix()
    {
        // lowering the nice value is privileged: root (Linux under systemd, and the Windows service's
        // Unix equivalents) gets it outright. An unprivileged macOS agent does not — there launchd is
        // the one that can, from the Nice key AgentCommands writes into the plist, and this call then
        // only has to match a value we already hold, which is permitted.
        if (SetPriority(PrioProcess, 0, UnixNice) == 0)
            return $"nice {UnixNice}";

        var errno = Marshal.GetLastPInvokeError();
        return $"unchanged (nice {GetPriority(PrioProcess, 0)}, setpriority errno {errno})";
    }
}
