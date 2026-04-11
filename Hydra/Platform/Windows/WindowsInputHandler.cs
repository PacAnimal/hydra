using System.Runtime.InteropServices;
using Cathedral.Utils;
using Hydra.Keyboard;
using Hydra.Mouse;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Windows;

public sealed class WindowsInputHandler(ILogger<WindowsInputHandler> log) : IPlatformInput
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
    private Action<MouseButtonEvent>? _onMouseButton;
    private Action<MouseScrollEvent>? _onMouseScroll;
    private readonly WindowsCursorSnapshot _cursor = new();
    private int _lastWarpX = -1;
    private int _lastWarpY = -1;
    private readonly Toggle _isOnVirtualScreen = new();
    public bool IsOnVirtualScreen { get => _isOnVirtualScreen; set => _isOnVirtualScreen.TrySet(value); }
    private nint _currentDesktop;
    private Timer? _healthTimer;

    // posted to the hook thread to trigger a desktop check
    private const uint WmCheckHealth = NativeMethods.WM_USER + 1;


    // low-level hooks work without elevation for non-elevated processes
    public bool IsAccessibilityTrusted() => true;

    public void WarpCursor(int x, int y)
    {
        _lastWarpX = x;
        _lastWarpY = y;
        NativeMethods.SetCursorPos(x, y);
    }

    public Task HideCursor() { _cursor.Hide(); return Task.CompletedTask; }

    public Task ShowCursor() { _cursor.Show(); return Task.CompletedTask; }

    public async Task StartEventTap(
        Action<double, double> onMouseMove,
        Action<double, double>? onMouseDelta,
        Action<KeyEvent> onKeyEvent,
        Action<MouseButtonEvent> onMouseButton,
        Action<MouseScrollEvent> onMouseScroll)
    {
        _onMouseMove = onMouseMove;
        _onKeyEvent = onKeyEvent;
        _onMouseButton = onMouseButton;
        _onMouseScroll = onMouseScroll;

        // callbacks stored as fields to prevent GC collection while hooks are active
        _mouseHookProc = MouseHookCallback;
        _keyboardHookProc = KeyboardHookCallback;

        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _hookThread = new Thread(() =>
        {
            _hookThreadId = NativeMethods.GetCurrentThreadId();
            // pass null for hMod — LL hooks don't need a module handle (matches all reference KVM projects)
            _mouseHook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_MOUSE_LL, _mouseHookProc, nint.Zero, 0);
            _keyboardHook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_KEYBOARD_LL, _keyboardHookProc, nint.Zero, 0);

            if (_mouseHook == nint.Zero || _keyboardHook == nint.Zero)
            {
                log.LogError("SetWindowsHookEx failed -- could not install input hooks");
                ready.TrySetResult(false);
                return;
            }

            _currentDesktop = NativeMethods.GetThreadDesktop(_hookThreadId);
            ready.TrySetResult(true);

            // message pump — hooks fire during GetMessage
            while (NativeMethods.GetMessage(out var msg, nint.Zero, 0, 0) > 0)
            {
                if (msg.message == WmCheckHealth)
                {
                    CheckHookHealth();
                    continue;
                }
                NativeMethods.TranslateMessage(in msg);
                NativeMethods.DispatchMessage(in msg);
            }

            if (_mouseHook != nint.Zero) NativeMethods.UnhookWindowsHookEx(_mouseHook);
            if (_keyboardHook != nint.Zero) NativeMethods.UnhookWindowsHookEx(_keyboardHook);
        })
        { IsBackground = true, Name = "HydraHookPump" };

        _hookThread.Start();
        await ready.Task;

        // periodic hook health check — detects desktop changes (UAC, lock screen) that silently invalidate hooks
        _healthTimer = new Timer(_ =>
            NativeMethods.PostThreadMessage(_hookThreadId, WmCheckHealth, nint.Zero, nint.Zero),
            null, 200, 200);
    }

    public unsafe KeyRepeatSettings GetKeyRepeatSettings()
    {
        uint delay = 1, speed = 15;
        NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETKEYBOARDDELAY, 0, (nint)(&delay), 0);
        NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETKEYBOARDSPEED, 0, (nint)(&speed), 0);

        // delay: 0=250ms, 1=500ms, 2=750ms, 3=1000ms
        var delayMs = (int)((delay + 1) * 250);
        // speed: 0=slowest (~500ms), 31=fastest (~33ms); linear interpolation
        var rateMs = (int)(500 - (speed * (500 - 33) / 31));
        return new KeyRepeatSettings(delayMs, rateMs);
    }

    public void StopEventTap()
    {
        _healthTimer?.Dispose();
        _healthTimer = null;
        if (_hookThreadId != 0)
            NativeMethods.PostThreadMessage(_hookThreadId, NativeMethods.WM_QUIT, nint.Zero, nint.Zero);
        _hookThread?.Join(TimeSpan.FromSeconds(2));
    }

    public void Dispose()
    {
        StopEventTap();
        _cursor.Dispose();
    }

    // called on the hook thread — checks if the desktop has changed and reinstalls hooks if needed
    private void CheckHookHealth()
    {
        var desk = NativeMethods.GetThreadDesktop(_hookThreadId);
        if (desk == _currentDesktop) return;
        _currentDesktop = desk;

        log.LogInformation("Desktop change detected, reinstalling hooks");
        if (_mouseHook != nint.Zero) { NativeMethods.UnhookWindowsHookEx(_mouseHook); _mouseHook = nint.Zero; }
        if (_keyboardHook != nint.Zero) { NativeMethods.UnhookWindowsHookEx(_keyboardHook); _keyboardHook = nint.Zero; }
        _mouseHook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_MOUSE_LL, _mouseHookProc!, nint.Zero, 0);
        _keyboardHook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_KEYBOARD_LL, _keyboardHookProc!, nint.Zero, 0);
        if (_mouseHook == nint.Zero || _keyboardHook == nint.Zero)
            log.LogWarning("Hook reinstall failed after desktop change");
    }

    private nint MouseHookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            var msg = (int)wParam;

            if (msg == NativeMethods.WM_MOUSEMOVE)
            {
                // ignore synthetic events generated by our own WarpCursor call
                if (info.pt.x == _lastWarpX && info.pt.y == _lastWarpY)
                    return IsOnVirtualScreen ? 1 : NativeMethods.CallNextHookEx(nint.Zero, nCode, wParam, lParam);
                var wasOnVirtualScreen = IsOnVirtualScreen;
                _onMouseMove?.Invoke(info.pt.x, info.pt.y);
                // swallow the event that triggered a virtual→real transition: _onMouseMove warped to the
                // entry edge, and passing this event through would move the cursor back (overriding the warp)
                if (wasOnVirtualScreen && !IsOnVirtualScreen) return 1;
            }
            else if (msg is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_LBUTTONUP
                or NativeMethods.WM_RBUTTONDOWN or NativeMethods.WM_RBUTTONUP
                or NativeMethods.WM_MBUTTONDOWN or NativeMethods.WM_MBUTTONUP
                or NativeMethods.WM_XBUTTONDOWN or NativeMethods.WM_XBUTTONUP)
            {
                var isDown = msg is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_RBUTTONDOWN
                    or NativeMethods.WM_MBUTTONDOWN or NativeMethods.WM_XBUTTONDOWN;
                var button = msg is NativeMethods.WM_XBUTTONDOWN or NativeMethods.WM_XBUTTONUP
                    ? (((info.mouseData >> 16) & 0xFFFF) == NativeMethods.XBUTTON1 ? MouseButton.Extra1 : MouseButton.Extra2)
                    : msg is NativeMethods.WM_RBUTTONDOWN or NativeMethods.WM_RBUTTONUP ? MouseButton.Right
                    : msg is NativeMethods.WM_MBUTTONDOWN or NativeMethods.WM_MBUTTONUP ? MouseButton.Middle
                    : MouseButton.Left;
                _onMouseButton?.Invoke(new MouseButtonEvent(button, isDown));
            }
            else if (msg is NativeMethods.WM_MOUSEWHEEL or NativeMethods.WM_MOUSEHWHEEL)
            {
                var delta = (short)(info.mouseData >> 16);
                var scroll = msg == NativeMethods.WM_MOUSEWHEEL
                    ? new MouseScrollEvent(0, delta)
                    : new MouseScrollEvent(delta, 0);
                _onMouseScroll?.Invoke(scroll);
            }
        }

        // swallow all mouse events while on virtual screen — cursor stays frozen at center
        if (IsOnVirtualScreen) return 1;
        return NativeMethods.CallNextHookEx(nint.Zero, nCode, wParam, lParam);
    }

    private nint KeyboardHookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            // always resolve to track modifier state even on the real screen
            var keyEvents = _keyResolver.Resolve((int)wParam, info);
            if (keyEvents is not null)
                foreach (var keyEvent in keyEvents)
                    _onKeyEvent?.Invoke(keyEvent);
            if (IsOnVirtualScreen) return 1; // swallow — don't call CallNextHookEx
        }
        return NativeMethods.CallNextHookEx(nint.Zero, nCode, wParam, lParam);
    }
}
