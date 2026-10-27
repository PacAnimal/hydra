using System.Runtime.InteropServices;
using Hydra.Screen;

namespace Hydra.Platform.Windows;

// Windows' half of IWindowFocus, run on whichever thread its caller owns: the hook loop on a master, where the shield
// lives (WindowsShieldWindow.FocusFrontmostOn), and the input desktop's thread on a slave.
internal static class WindowsWindowFocus
{
    // whether a window was there to focus
    internal static bool FocusFrontmostOn(ScreenBounds screen)
    {
        if (WindowPicker.FrontmostOn(TopLevelWindows(), screen) is not { } window) return false;
        Activate(window.Id);
        return true;
    }

    // EnumWindows lists top to bottom; a window counts if it shows on the taskbar's terms: visible, not minimised, not a
    // tool or no-activate window, unowned, not cloaked, and not ours
    private static unsafe List<ListedWindow> TopLevelWindows()
    {
        var all = new List<nint>();
        var handle = GCHandle.Alloc(all);
        try
        {
            _ = NativeMethods.EnumWindows(&Collect, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }
        var self = (uint)Environment.ProcessId;
        var listed = new List<ListedWindow>();
        foreach (var hwnd in all)
        {
            if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd)) continue;
            var exStyle = (uint)NativeMethods.GetWindowLongPtrW(hwnd, NativeMethods.GWL_EXSTYLE);
            if ((exStyle & (NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE)) != 0) continue;
            if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != 0) continue;
            if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0) continue;
            _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == self || !NativeMethods.GetWindowRect(hwnd, out var r)) continue;
            listed.Add(new ListedWindow(hwnd, (int)pid, new ScreenBounds(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top)));
        }
        return listed;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static int Collect(nint hwnd, nint state)
    {
        ((List<nint>)GCHandle.FromIntPtr(state).Target!).Add(hwnd);
        return 1;
    }

    // SetForegroundWindow is refused to a process that is not in the foreground; sharing the foreground thread's input
    // state for the call lifts that
    private static void Activate(nint hwnd)
    {
        var foregroundThread = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out _);
        var ownThread = NativeMethods.GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != ownThread && NativeMethods.AttachThreadInput(ownThread, foregroundThread, true);
        try
        {
            _ = NativeMethods.BringWindowToTop(hwnd);
            _ = NativeMethods.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) _ = NativeMethods.AttachThreadInput(ownThread, foregroundThread, false);
        }
    }
}
