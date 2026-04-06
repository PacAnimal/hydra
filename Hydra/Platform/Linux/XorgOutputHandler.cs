using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Platform;
using Hydra.Relay;
using Hydra.Screen;

namespace Hydra.Platform.Linux;

public sealed class XorgOutputHandler : IPlatformOutput, ICursorVisibility
{
    private bool _cursorHidden;
    private readonly nint _display;
    private readonly int _screen;
    private readonly nint _rootWindow;
    private bool _disposed;
    public XorgOutputHandler()
    {
        _ = NativeMethods.XInitThreads();
        _display = NativeMethods.XOpenDisplay(null);
        if (_display == nint.Zero)
            throw new InvalidOperationException("XOpenDisplay failed — is DISPLAY set?");

        _screen = NativeMethods.XDefaultScreen(_display);
        _rootWindow = NativeMethods.XDefaultRootWindow(_display);
        // allow XTest events during active grabs (e.g. fullscreen games)
        _ = NativeMethods.XTestGrabControl(_display, true);
    }


    public void MoveMouse(int x, int y)
    {
        _ = NativeMethods.XTestFakeMotionEvent(_display, _screen, x, y, 0);
        _ = NativeMethods.XFlush(_display);
    }

    public void MoveMouseRelative(int dx, int dy)
    {
        // XTestFakeRelativeMotionEvent generates a proper raw input event (XI_RawMotion),
        // which games see. XWarpPointer is a cursor warp only and is invisible to raw input.
        _ = NativeMethods.XTestFakeRelativeMotionEvent(_display, dx, dy, 0);
        _ = NativeMethods.XFlush(_display);
    }

    public void InjectKey(KeyEventMessage msg)
    {
        var isDown = msg.Type == KeyEventType.KeyDown;

        if (msg.Character is { } ch)
        {
            // unicode char → x11 keysym → keycode
            var keysym = ch <= '\xFF' ? (ulong)ch : 0x01000000u | (uint)ch;
            InjectKeysym(keysym, isDown);
        }
        else if (msg.Key is { } key)
        {
            var keysym = SpecialKeyToKeysym(key);
            if (keysym != 0)
                InjectKeysym(keysym, isDown);
        }
    }

    public void InjectMouseButton(MouseButtonMessage msg)
    {
        var button = msg.Button switch
        {
            MouseButton.Left => 1u,
            MouseButton.Middle => 2u,
            MouseButton.Right => 3u,
            MouseButton.Extra1 => 8u,
            MouseButton.Extra2 => 9u,
            _ => 0u,
        };
        if (button == 0) return;

        _ = NativeMethods.XTestFakeButtonEvent(_display, button, msg.IsPressed, 0);
        _ = NativeMethods.XFlush(_display);
    }

    public void InjectMouseScroll(MouseScrollMessage msg)
    {
        // x11 scroll: buttons 4=up, 5=down, 6=left, 7=right (each press = one 120-unit click)
        InjectScrollAxis(4u, 5u, msg.YDelta / 120);
        InjectScrollAxis(7u, 6u, msg.XDelta / 120);
        _ = NativeMethods.XFlush(_display);
    }

    private void InjectScrollAxis(uint positiveButton, uint negativeButton, int clicks)
    {
        if (clicks == 0) return;
        var button = clicks > 0 ? positiveButton : negativeButton;
        var n = Math.Abs(clicks);
        for (var i = 0; i < n; i++)
        {
            _ = NativeMethods.XTestFakeButtonEvent(_display, button, true, 0);
            _ = NativeMethods.XTestFakeButtonEvent(_display, button, false, 0);
        }
    }

    private void InjectKeysym(ulong keysym, bool isDown)
    {
        var keycode = NativeMethods.XKeysymToKeycode(_display, keysym);
        if (keycode == 0) return;
        _ = NativeMethods.XTestFakeKeyEvent(_display, keycode, isDown, 0);
        _ = NativeMethods.XFlush(_display);
    }

    private static ulong SpecialKeyToKeysym(SpecialKey key)
    {
        // media keys and other non-MISCELLANY keys: reverse map via XorgSpecialKeyMap
        if (XorgSpecialKeyMap.Instance.Reverse.TryGetValue(key, out var keysym))
            return keysym;

        // MISCELLANY keys: SpecialKey value encodes (keysym | 0x01000000), strip the flag
        var raw = (uint)key;
        if ((raw & 0xFF000000u) == 0x01000000u)
            return raw & 0x00FFFFFFu;

        return 0;
    }

    public void HideCursor()
    {
        if (_cursorHidden) return;
        NativeMethods.XFixesHideCursor(_display, _rootWindow);
        _ = NativeMethods.XFlush(_display);
        _cursorHidden = true;
    }

    public void ShowCursor()
    {
        if (!_cursorHidden) return;
        NativeMethods.XFixesShowCursor(_display, _rootWindow);
        _ = NativeMethods.XFlush(_display);
        _cursorHidden = false;
    }

    public CursorPosition GetCursorPosition()
    {
        NativeMethods.XQueryPointer(_display, _rootWindow,
            out _, out _, out var rootX, out var rootY, out _, out _, out uint _);
        return new CursorPosition(rootX, rootY);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
        if (_display != nint.Zero)
        {
            if (_cursorHidden)
                NativeMethods.XFixesShowCursor(_display, _rootWindow);
            _ = NativeMethods.XCloseDisplay(_display);
        }
    }
}
