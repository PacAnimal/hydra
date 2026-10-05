using System.ComponentModel;
using System.Diagnostics;

namespace Tests.Setup;

internal static class LiveProcess
{
    // a reused pid only makes this answer yes a little longer, which costs a leftover and never a live run's files
    internal static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return HasNotExited(process, static p => p.HasExited);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    // windows refuses HasExited on a protected process; one we cannot ask about is presumed running
    internal static bool HasNotExited<T>(T process, Func<T, bool> hasExited)
    {
        try { return !hasExited(process); }
        catch (Win32Exception) { return true; }
    }
}
