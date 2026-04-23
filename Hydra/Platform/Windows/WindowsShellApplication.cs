using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Hydra.Platform.Windows;

// cached Shell.Application COM instance with lazy init and stale-object recovery.
// callers must hold Lock before calling GetShellUnderLock or InvalidateUnderLock.
[SupportedOSPlatform("windows")]
internal sealed class WindowsShellApplication : IDisposable
{
    public readonly Lock Lock = new();
    private Type? _shellType;
    private object? _shell;

    // caller must hold Lock
    public (Type?, object?) GetShellUnderLock()
    {
        if (_shell != null) return (_shellType, _shell);
        _shellType = Type.GetTypeFromProgID("Shell.Application");
        if (_shellType == null) return (null, null);
        _shell = Activator.CreateInstance(_shellType);
        return (_shellType, _shell);
    }

    // caller must hold Lock — releases the stale shell so next call recreates it
    public void InvalidateUnderLock()
    {
        var stale = _shell;
        _shell = null;
        if (stale != null) TryRelease(stale);
    }

    public void Dispose()
    {
        object? shell;
        lock (Lock) { shell = _shell; _shell = null; }
        if (shell != null) TryRelease(shell);
    }

    public static void TryRelease(object obj)
    {
        try { Marshal.ReleaseComObject(obj); }
        catch { /* already released */ }
    }
}
