using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Hydra.FileTransfer;
using Microsoft.CSharp.RuntimeBinder;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Windows;

// detects files selected in the foreground Explorer window via Shell.Application COM.
// the Shell.Application instance is cached and recreated on COM failure.
[SupportedOSPlatform("windows")]
public sealed class WindowsFileSelectionDetector(ILogger<WindowsFileSelectionDetector> log) : IFileSelectionDetector, IDisposable
{
    private readonly ILogger<WindowsFileSelectionDetector> _log = log;
    private readonly Lock _lock = new();
    private Type? _shellType;
    private object? _shell;

    public string FileManagerName => "Explorer";
    public bool IsFileTransferSupported => true;

    public FileSelectionResult GetSelectedPaths()
    {
        try
        {
            return FindSelectedInForegroundExplorer();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "GetSelectedPaths failed");
            return new FileSelectionResult(false, null);
        }
    }

    public void Dispose()
    {
        object? shell;
        lock (_lock) { shell = _shell; _shell = null; }
        if (shell != null) TryReleaseComObject(shell);
    }

    private FileSelectionResult FindSelectedInForegroundExplorer()
    {
        var fgHwnd = NativeMethods.GetForegroundWindow();
        if (fgHwnd == nint.Zero) return new FileSelectionResult(false, null);

        // walk to top-level window (Explorer cabinet window)
        var rootHwnd = NativeMethods.GetAncestor(fgHwnd, NativeMethods.GA_ROOTOWNER);

        lock (_lock)
        {
            var (shellType, shell) = GetShellUnderLock();
            if (shell == null || shellType == null) return new FileSelectionResult(false, null);

            dynamic? windows = null;
            try
            {
                windows = shellType.InvokeMember("Windows", System.Reflection.BindingFlags.InvokeMethod, null, shell, null)!;
                int count = windows.Count;
                for (var i = 0; i < count; i++)
                {
                    dynamic? window = windows.Item(i);
                    if (window == null) continue;
                    try
                    {
                        nint hwnd;
                        try { hwnd = (nint)window.HWND; }
                        catch { continue; }

                        // for regular Explorer windows hwnd == rootHwnd directly;
                        // for the Desktop, the shell window reports a child HWND (SHELLDLL_DefView)
                        // whose root ancestor is Progman, so normalise both sides before comparing
                        if (hwnd != rootHwnd && NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT) != rootHwnd) continue;

                        // matched foreground HWND to an Explorer window — Explorer is focused
                        dynamic? items;
                        try { items = window.Document?.SelectedItems(); }
                        catch { continue; }
                        if (items == null) return new FileSelectionResult(true, null);

                        var paths = new List<string>();
                        try
                        {
                            int itemCount = items.Count;
                            for (var j = 0; j < itemCount; j++)
                            {
                                dynamic? item = items.Item(j);
                                if (item == null) continue;
                                string? path;
                                try { path = item.Path as string; }
                                catch { path = null; }
                                finally { TryReleaseComObject(item); }
                                if (path != null) paths.Add(path);
                            }
                        }
                        finally { TryReleaseComObject(items); }

                        return new FileSelectionResult(true, paths.Count > 0 ? paths : null);
                    }
                    finally { TryReleaseComObject(window); }
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or RuntimeBinderException)
            {
                // cached shell object became stale — release it and recreate next call
                var stale = _shell;
                _shell = null;
                if (stale != null) TryReleaseComObject(stale);
                _log.LogDebug(ex, "Shell.Application COM object became stale — will recreate on next call");
            }
            finally
            {
                if (windows != null) TryReleaseComObject(windows);
            }
        }

        // no Shell window matched the foreground HWND — Explorer is not focused
        return new FileSelectionResult(false, null);
    }

    // caller must hold _lock
    private (Type?, object?) GetShellUnderLock()
    {
        if (_shell != null) return (_shellType, _shell);
        _shellType = Type.GetTypeFromProgID("Shell.Application");
        if (_shellType == null) return (null, null);
        _shell = Activator.CreateInstance(_shellType);
        return (_shellType, _shell);
    }

    private static void TryReleaseComObject(object obj)
    {
        try { Marshal.ReleaseComObject(obj); }
        catch { /* already released */ }
    }
}
