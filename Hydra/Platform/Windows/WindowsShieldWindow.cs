using System.Runtime.InteropServices;

namespace Hydra.Platform.Windows;

// topmost invisible window that steals foreground focus when active on a virtual screen,
// preventing hover effects (taskbar highlights, tooltips, button states) on the master.
// also owns cursor visibility via ShowCursor loops (true hide, not 1x1 blank replacement).
// must only be called from the thread that owns the message pump (HydraHookPump).
internal sealed class WindowsShieldWindow
{
    private nint _hwnd;
    private nint _savedForeground;
    private WndProc? _wndProc; // prevent GC while window exists
    private nint _debugBrush;
    private bool _debugShield;
    private bool _cursorHidden;

    internal void Create(bool debugShield)
    {
        _debugShield = debugShield;
        _wndProc = WndProcImpl;

        var hInstance = NativeMethods.GetModuleHandleW(nint.Zero);
        var className = Marshal.StringToHGlobalUni("HydraShield");
        try
        {
            if (debugShield)
                _debugBrush = NativeMethods.CreateSolidBrush(0x000000FF); // red (BGR)

            var wc = new NativeMethods.WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = hInstance,
                hbrBackground = _debugBrush,
                lpszClassName = className,
            };
            var atom = NativeMethods.RegisterClassExW(in wc);
            if (atom == 0) return;

            var exStyle = NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST;
            if (debugShield) exStyle |= NativeMethods.WS_EX_LAYERED;

            _hwnd = NativeMethods.CreateWindowExW(
                exStyle,
                atom, nint.Zero,
                NativeMethods.WS_POPUP | NativeMethods.WS_DISABLED,
                0, 0, 1, 1,
                nint.Zero, nint.Zero, hInstance, nint.Zero);

            if (_hwnd == nint.Zero) return;

            if (debugShield)
                NativeMethods.SetLayeredWindowAttributes(_hwnd, 0, 128, NativeMethods.LWA_ALPHA);
        }
        finally
        {
            Marshal.FreeHGlobal(className);
        }
    }

    internal void Show(int x, int y)
    {
        if (_hwnd == nint.Zero) return;

        _savedForeground = NativeMethods.GetForegroundWindow();

        var size = _debugShield ? 200 : 32;
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST,
            x - size / 2, y - size / 2, size, size,
            NativeMethods.SWP_SHOWWINDOW);

        NativeMethods.EnableWindow(_hwnd, true);
        NativeMethods.SetActiveWindow(_hwnd);
        NativeMethods.SetForegroundWindow(_hwnd);

        if (!_debugShield)
            HideCursorCounter();
    }

    internal void Hide()
    {
        if (_hwnd == nint.Zero) return;

        ShowCursorCounter();

        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_BOTTOM, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_HIDEWINDOW);
        NativeMethods.EnableWindow(_hwnd, false);

        if (_savedForeground != nint.Zero)
            NativeMethods.SetForegroundWindow(_savedForeground);
        _savedForeground = nint.Zero;
    }

    internal void Destroy()
    {
        if (_cursorHidden) ShowCursorCounter();
        if (_hwnd != nint.Zero) { NativeMethods.DestroyWindow(_hwnd); _hwnd = nint.Zero; }
        if (_debugBrush != nint.Zero) { NativeMethods.DeleteObject(_debugBrush); _debugBrush = nint.Zero; }
    }

    // loop until display counter goes negative (cursor hidden)
    private void HideCursorCounter()
    {
        if (_cursorHidden) return;
        for (var i = 0; i < 10; i++)
            if (NativeMethods.ShowCursorWin32(false) < 0) break;
        _cursorHidden = true;
    }

    // loop until display counter is non-negative (cursor visible)
    private void ShowCursorCounter()
    {
        if (!_cursorHidden) return;
        for (var i = 0; i < 10; i++)
            if (NativeMethods.ShowCursorWin32(true) >= 0) break;
        _cursorHidden = false;
    }

    private static nint WndProcImpl(nint hWnd, uint msg, nint wParam, nint lParam) =>
        NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
}
