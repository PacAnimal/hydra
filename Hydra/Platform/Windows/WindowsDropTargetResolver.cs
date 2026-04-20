using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Hydra.FileTransfer;
using Microsoft.CSharp.RuntimeBinder;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Windows;

// resolves the paste destination by finding which Explorer window is in the foreground,
// via Shell.Application COM. the Shell.Application instance is cached and recreated on COM failure.
[SupportedOSPlatform("windows")]
public sealed class WindowsDropTargetResolver(ILogger<WindowsDropTargetResolver> log) : IDropTargetResolver, IDisposable
{
    private readonly Lock _lock = new();
    private Type? _shellType;
    private object? _shell;

    public string? GetPasteDirectory()
    {
        try
        {
            return FindExplorerFolderForForegroundWindow();
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "GetPasteDirectory failed");
            return null;
        }
    }

    public void Dispose()
    {
        object? shell;
        lock (_lock) { shell = _shell; _shell = null; }
        if (shell != null) TryReleaseComObject(shell);
    }

    public void MoveToDestination(string tempDir, string destDir)
    {
        var entries = Directory.GetFileSystemEntries(tempDir);
        if (entries.Length == 0) return;

        // double-null-terminated source list required by SHFileOperation
        var from = string.Join('\0', entries) + "\0\0";
        var to = destDir + "\0\0";
        var op = new NativeMethods.SHFILEOPSTRUCTW
        {
            wFunc = NativeMethods.FO_MOVE,
            pFrom = from,
            pTo = to,
            fFlags = NativeMethods.FOF_NOCONFIRMMKDIR | NativeMethods.FOF_ALLOWUNDO,
        };

        // SHFileOperation requires STA thread for its conflict dialog message pump
        int moveResult = 0;
        var thread = new Thread(() =>
        {
            _ = NativeMethods.OleInitialize(nint.Zero);
            try { moveResult = NativeMethods.SHFileOperationW(ref op); }
            finally { NativeMethods.OleUninitialize(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30)))
        {
            // SHFileOperationW's STA message pump deadlocked (e.g. conflict dialog blocked by another stuck STA thread)
            log.LogWarning("SHFileOperationW timed out — falling back to managed move");
            FileUtils.MoveTo(tempDir, destDir);
            return;
        }
        if (moveResult != 0)
        {
            log.LogWarning("SHFileOperationW failed (0x{Result:X}), falling back to managed move", moveResult);
            FileUtils.MoveTo(tempDir, destDir);
        }
    }

    private string? FindExplorerFolderForForegroundWindow()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == nint.Zero) return null;

        // walk up to the top-level window (Explorer's cabinet window)
        var rootHwnd = NativeMethods.GetAncestor(foreground, NativeMethods.GA_ROOTOWNER);

        // desktop window (Progman/WorkerW) → paste to Desktop folder
        var rootClass = GetWindowClass(rootHwnd);
        if (rootClass is "Progman" or "WorkerW")
            return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

        // hold the lock for the entire enumeration so InvalidateShell() can't release the
        // shell object while we're mid-enumeration on another thread
        lock (_lock)
        {
            var (shellType, shell) = GetShellUnderLock();
            if (shell == null || shellType == null) return null;

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
                        // IWebBrowserApp.HWND is VT_I4 (int32) from the COM type library
                        nint hwnd;
                        try { hwnd = (nint)window.HWND; }
                        catch { continue; }

                        if (hwnd != rootHwnd) continue;

                        // LocationURL is a file:// URL of the displayed folder
                        string? locationUrl;
                        try { locationUrl = window.LocationURL as string; }
                        catch { continue; }

                        if (locationUrl == null) continue;
                        var localPath = FileUtils.FileUrlToLocalPath(locationUrl);
                        if (localPath != null) return localPath;
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
                log.LogDebug(ex, "Shell.Application COM object became stale — will recreate on next call");
            }
            finally
            {
                if (windows != null) TryReleaseComObject(windows);
            }
        }

        return null;
    }

    private static unsafe string GetWindowClass(nint hwnd)
    {
        const int maxLen = 128;
        char* buf = stackalloc char[maxLen];
        var len = NativeMethods.GetClassNameW(hwnd, buf, maxLen);
        return len > 0 ? new string(buf, 0, len) : string.Empty;
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

    private static void TryReleaseComObject(object shell)
    {
        try { Marshal.ReleaseComObject(shell); }
        catch { /* already released */ }
    }
}
