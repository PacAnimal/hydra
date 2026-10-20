using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Hydra.Platform;

// raises this process to High / nice -10: short of realtime, which would pre-empt the threads that deliver our events
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
        // lowering the nice value is privileged: root gets it outright, and a macOS agent already holds
        // it from the Nice key AgentPlist writes, which this call is then permitted to match
        if (SetPriority(PrioProcess, 0, UnixNice) == 0)
            return $"nice {UnixNice}";

        var errno = Marshal.GetLastPInvokeError();
        return $"unchanged (nice {GetPriority(PrioProcess, 0)}, setpriority errno {errno})";
    }
}
