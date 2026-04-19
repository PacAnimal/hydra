using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Hydra.FileTransfer;
using Microsoft.CSharp.RuntimeBinder;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Windows;

// detects files selected in the foreground Explorer window via Shell.Application COM.
// falls back to reading the desktop SysListView32 directly when the desktop isn't in the COM collection.
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

        // on some Windows versions the desktop's foreground window is Progman/WorkerW
        var fgIsDesktop = IsDesktopHwnd(rootHwnd);

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

                        // regular Explorer: direct HWND match
                        // Desktop (SHELLDLL_DefView case): child HWND whose GA_ROOT is Progman
                        // Desktop (modern Windows): some versions report HWND=0 for the desktop entry
                        if (hwnd != rootHwnd
                            && NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT) != rootHwnd
                            && !(hwnd == 0 && fgIsDesktop)) continue;

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

        // Shell.Application.Windows() didn't include the desktop — fall back to direct ListView query
        if (fgIsDesktop)
            return GetDesktopSelectionFromListView();

        // no Shell window matched the foreground HWND — Explorer is not focused
        return new FileSelectionResult(false, null);
    }

    // reads selected items directly from the desktop SysListView32 via cross-process memory.
    // this covers Windows versions where the desktop is absent from Shell.Application.Windows().
    private FileSelectionResult GetDesktopSelectionFromListView()
    {
        var hListView = FindDesktopListView();
        if (hListView == 0) return new FileSelectionResult(true, null);

        var selCount = NativeMethods.SendMessageW(hListView, NativeMethods.LVM_GETSELECTEDCOUNT, 0, 0);
        if (selCount == 0) return new FileSelectionResult(true, null);

        // collect selected indices — LVM_GETNEXTITEM returns plain ints, no cross-process memory needed
        var selectedIndices = new List<int>();
        var idx = -1;
        while (true)
        {
            idx = (int)NativeMethods.SendMessageW(hListView, NativeMethods.LVM_GETNEXTITEM, idx, NativeMethods.LVNI_SELECTED);
            if (idx == -1) break;
            selectedIndices.Add(idx);
        }
        if (selectedIndices.Count == 0) return new FileSelectionResult(true, null);

        // get item display names via cross-process memory + LVM_GETITEMTEXTW
        NativeMethods.GetWindowThreadProcessId(hListView, out var pid);
        var hProcess = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_VM_READ | NativeMethods.PROCESS_VM_WRITE | NativeMethods.PROCESS_VM_OPERATION,
            false, pid);
        if (hProcess == 0) return new FileSelectionResult(true, null);

        try
        {
            const int maxChars = 260;
            var structSize = Marshal.SizeOf<LvItemText>();
            var bufSize = structSize + maxChars * 2; // LVITEM + unicode text buffer
            var pBuf = NativeMethods.VirtualAllocEx(hProcess, 0, (nuint)bufSize,
                NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE, NativeMethods.PAGE_READWRITE);
            if (pBuf == 0) return new FileSelectionResult(true, null);

            try
            {
                var userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                var commonDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
                var paths = new List<string>();

                foreach (var index in selectedIndices)
                {
                    var name = ReadListViewItemText(hProcess, hListView, pBuf, structSize, index, maxChars);
                    if (string.IsNullOrEmpty(name)) continue;

                    var path = Path.Combine(userDesktop, name);
                    if (!File.Exists(path) && !Directory.Exists(path))
                        path = Path.Combine(commonDesktop, name);
                    if (!File.Exists(path) && !Directory.Exists(path))
                        continue; // virtual item (Recycle Bin, This PC, etc.)

                    paths.Add(path);
                }

                return new FileSelectionResult(true, paths.Count > 0 ? paths : null);
            }
            finally
            {
                NativeMethods.VirtualFreeEx(hProcess, pBuf, 0, NativeMethods.MEM_RELEASE);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(hProcess);
        }
    }

    // writes an LVITEM to the remote process, sends LVM_GETITEMTEXTW, reads the result back
    private static unsafe string ReadListViewItemText(nint hProcess, nint hListView, nint pBuf, int structSize, int index, int maxChars)
    {
        var lvItem = new LvItemText
        {
            iSubItem = 0,
            pszText = pBuf + structSize, // text buffer follows the struct in remote memory
            cchTextMax = maxChars,
        };

        nuint written;
        NativeMethods.WriteProcessMemory(hProcess, pBuf, &lvItem, (nuint)structSize, out written);
        if (written == 0) return string.Empty;

        NativeMethods.SendMessageW(hListView, NativeMethods.LVM_GETITEMTEXTW, index, pBuf);

        var textBytes = new byte[maxChars * 2];
        nuint read;
        fixed (byte* p = textBytes)
            NativeMethods.ReadProcessMemory(hProcess, pBuf + structSize, p, (nuint)textBytes.Length, out read);

        return Encoding.Unicode.GetString(textBytes).TrimEnd('\0');
    }

    // finds Progman → SHELLDLL_DefView → SysListView32, then tries WorkerW siblings
    private static nint FindDesktopListView()
    {
        var progman = NativeMethods.FindWindowW("Progman", null);
        if (progman != 0)
        {
            var defView = NativeMethods.FindWindowExW(progman, 0, "SHELLDLL_DefView", null);
            if (defView != 0)
            {
                var lv = NativeMethods.FindWindowExW(defView, 0, "SysListView32", null);
                if (lv != 0) return lv;
            }
        }

        // on some Windows versions the icons live under a WorkerW sibling of Progman
        var workerW = NativeMethods.FindWindowExW(0, 0, "WorkerW", null);
        while (workerW != 0)
        {
            var defView = NativeMethods.FindWindowExW(workerW, 0, "SHELLDLL_DefView", null);
            if (defView != 0)
            {
                var lv = NativeMethods.FindWindowExW(defView, 0, "SysListView32", null);
                if (lv != 0) return lv;
            }
            workerW = NativeMethods.FindWindowExW(0, workerW, "WorkerW", null);
        }

        return 0;
    }

    private static unsafe bool IsDesktopHwnd(nint hwnd)
    {
        const int maxLen = 64;
        char* cls = stackalloc char[maxLen];
        int len = NativeMethods.GetClassNameW(hwnd, cls, maxLen);
        var name = new ReadOnlySpan<char>(cls, len);
        return name.SequenceEqual("Progman") || name.SequenceEqual("WorkerW");
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

    // LVITEM layout for LVM_GETITEMTEXTW — explicit offsets match the 64-bit Windows LVITEMW struct.
    // only fields needed for text retrieval are defined; the struct is intentionally truncated.
    [StructLayout(LayoutKind.Explicit)]
    private struct LvItemText
    {
        [FieldOffset(0)] public uint mask;
        [FieldOffset(4)] public int iItem;
        [FieldOffset(8)] public int iSubItem;
        [FieldOffset(12)] public uint state;
        [FieldOffset(16)] public uint stateMask;
        [FieldOffset(24)] public nint pszText;   // 8-byte aligned on 64-bit
        [FieldOffset(32)] public int cchTextMax;
    }
}
