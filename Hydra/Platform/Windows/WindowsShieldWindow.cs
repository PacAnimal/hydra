namespace Hydra.Platform.Windows;

// topmost window, centred on the primary screen, shown while the cursor is on a virtual screen.
// steals foreground focus so other windows stop receiving hover events.
// handles WM_SETCURSOR to hide the cursor (true invisible, not 1x1 blank replacement).
// must only be called from the thread that owns the message pump (HydraHookPump).
internal sealed class WindowsShieldWindow
{
    private Win32Window? _window;
    private nint _savedForeground;
    private nint _debugBrush;
    private bool _debugShield;
    private readonly WindowsCursorSnapshot _cursor = new();

    private nint Hwnd => _window?.Handle ?? nint.Zero;

    // throws Win32Exception if the window cannot be made
    internal void Create(bool debugShield)
    {
        _debugShield = debugShield;
        if (debugShield)
            _debugBrush = NativeMethods.CreateSolidBrush(0x000000FF); // red (BGR)

        var exStyle = NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST;
        if (debugShield) exStyle |= NativeMethods.WS_EX_LAYERED;

        // start hidden, not disabled: a disabled window can't be activated or set foreground
        _window = new Win32Window("HydraShield", WndProcImpl, NativeMethods.WS_POPUP, exStyle, background: _debugBrush);

        if (debugShield)
            NativeMethods.SetLayeredWindowAttributes(Hwnd, 0, 128, NativeMethods.LWA_ALPHA);
    }

    internal void Show()
    {
        if (Hwnd == nint.Zero) return;

        // don't show while a fullscreen app (e.g. a game) is active — avoid anti-cheat detection
        _savedForeground = NativeMethods.GetForegroundWindow();
        if (IsFullscreenAppActive(_savedForeground)) return;

        // match mac shield: centered on main screen, 20% of screen dimensions
        var sw = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
        var sh = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
        var w = (int)(sw * 0.2);
        var h = (int)(sh * 0.2);
        var x = (sw - w) / 2;
        var y = (sh - h) / 2;

        NativeMethods.SetWindowPos(Hwnd, NativeMethods.HWND_TOPMOST, x, y, w, h, NativeMethods.SWP_SHOWWINDOW);

        NativeMethods.SetActiveWindow(Hwnd);
        NativeMethods.SetForegroundWindow(Hwnd);
    }

    // called inline from HideCursor()/ShowCursor() — SetSystemCursor is system-wide, safe inside a hook callback
    internal void HideCursorNow()
    {
        if (!_debugShield) _cursor.Hide();
    }

    internal void ShowCursorNow()
    {
        if (!_debugShield) _cursor.Show();
    }

    internal void Hide()
    {
        if (Hwnd == nint.Zero) return;

        NativeMethods.ShowWindow(Hwnd, NativeMethods.SW_HIDE);
        NativeMethods.SetWindowPos(Hwnd, NativeMethods.HWND_BOTTOM, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_HIDEWINDOW);

        if (_savedForeground != nint.Zero)
            NativeMethods.SetForegroundWindow(_savedForeground);
        _savedForeground = nint.Zero;
    }

    internal void Destroy()
    {
        _cursor.Dispose();
        _window?.Dispose();
        _window = null;
        if (_debugBrush != nint.Zero) { NativeMethods.DeleteObject(_debugBrush); _debugBrush = nint.Zero; }
    }

    // foreground window covers the full primary screen — likely a fullscreen game or exclusive app
    private static bool IsFullscreenAppActive(nint hwnd)
    {
        if (hwnd == nint.Zero) return false;
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return false;
        var sw = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
        var sh = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
        return rect.Left <= 0 && rect.Top <= 0 && rect.Right >= sw && rect.Bottom >= sh;
    }

    private nint WndProcImpl(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        // hide cursor whenever Windows asks what cursor to show over this window
        // in debug mode, let DefWindowProc handle it so the cursor remains visible
        if (!_debugShield && msg == NativeMethods.WM_SETCURSOR)
        {
            NativeMethods.SetCursor(nint.Zero);
            return 1;
        }
        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }
}
