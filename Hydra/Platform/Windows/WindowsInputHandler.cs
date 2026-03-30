using System.Runtime.InteropServices;
using Hydra.Keyboard;
using Hydra.Screen;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Windows;

public class WindowsInputHandler(ILogger<WindowsInputHandler> log) : IPlatformInput
{
    // stored as fields to prevent GC collection while hooks are active
    private HookProc? _mouseHookProc;
    private HookProc? _keyboardHookProc;
    private nint _mouseHook;
    private nint _keyboardHook;
    private Thread? _hookThread;
    private uint _hookThreadId;
    private readonly WinKeyResolver _keyResolver = new();
    private Action<double, double>? _onMouseMove;
    private Action<KeyEvent>? _onKeyEvent;
    private bool _cursorHidden;
    public bool IsOnVirtualScreen { get; set; }

    public ScreenRect GetPrimaryScreenBounds()
    {
        var w = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
        var h = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
        return new ScreenRect("main", 0, 0, w, h, false);
    }

    // low-level hooks work without elevation for non-elevated processes
    public bool IsAccessibilityTrusted() => true;

    public void WarpCursor(int x, int y) => NativeMethods.SetCursorPos(x, y);

    public void HideCursor()
    {
        if (_cursorHidden) return;
        NativeMethods.ShowCursor(false);
        _cursorHidden = true;
    }

    public void ShowCursor()
    {
        if (!_cursorHidden) return;
        NativeMethods.ShowCursor(true);
        _cursorHidden = false;
    }

    public void StartEventTap(Action<double, double> onMouseMove, Action<KeyEvent> onKeyEvent)
    {
        _onMouseMove = onMouseMove;
        _onKeyEvent = onKeyEvent;

        // callbacks stored as fields to prevent GC collection while hooks are active
        _mouseHookProc = MouseHookCallback;
        _keyboardHookProc = KeyboardHookCallback;

        var ready = new ManualResetEventSlim(false);

        _hookThread = new Thread(() =>
        {
            _hookThreadId = NativeMethods.GetCurrentThreadId();
            // pass null for hMod — LL hooks don't need a module handle (matches all reference KVM projects)
            _mouseHook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_MOUSE_LL, _mouseHookProc, nint.Zero, 0);
            _keyboardHook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_KEYBOARD_LL, _keyboardHookProc, nint.Zero, 0);

            if (_mouseHook == nint.Zero || _keyboardHook == nint.Zero)
            {
                log.LogError("SetWindowsHookEx failed -- could not install input hooks");
                ready.Set();
                return;
            }

            ready.Set();

            // message pump — hooks fire during GetMessage
            while (NativeMethods.GetMessage(out var msg, nint.Zero, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(in msg);
                NativeMethods.DispatchMessage(in msg);
            }

            if (_mouseHook != nint.Zero) NativeMethods.UnhookWindowsHookEx(_mouseHook);
            if (_keyboardHook != nint.Zero) NativeMethods.UnhookWindowsHookEx(_keyboardHook);
        })
        { IsBackground = true, Name = "HydraHookPump" };

        _hookThread.Start();
        ready.Wait();
    }

    public void StopEventTap()
    {
        if (_hookThreadId != 0)
            NativeMethods.PostThreadMessage(_hookThreadId, NativeMethods.WM_QUIT, nint.Zero, nint.Zero);
        _hookThread?.Join(TimeSpan.FromSeconds(2));
    }

    public void Dispose()
    {
        StopEventTap();
        if (_cursorHidden) ShowCursor();
        GC.SuppressFinalize(this);
    }

    private nint MouseHookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && wParam == NativeMethods.WM_MOUSEMOVE)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            _onMouseMove?.Invoke(info.pt.x, info.pt.y);
        }
        // mouse events always pass through
        return NativeMethods.CallNextHookEx(nint.Zero, nCode, wParam, lParam);
    }

    private nint KeyboardHookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            // always resolve to track modifier state even on the real screen
            var keyEvent = _keyResolver.Resolve((int)wParam, info);
            if (keyEvent is not null)
                _onKeyEvent?.Invoke(keyEvent);
            if (IsOnVirtualScreen) return 1; // swallow — don't call CallNextHookEx
        }
        return NativeMethods.CallNextHookEx(nint.Zero, nCode, wParam, lParam);
    }
}
