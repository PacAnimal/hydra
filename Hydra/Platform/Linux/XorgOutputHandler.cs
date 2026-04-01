using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Relay;
using Hydra.Screen;

namespace Hydra.Platform.Linux;

public sealed class XorgOutputHandler : IPlatformOutput
{
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
    }

    public ScreenRect GetPrimaryScreenBounds()
    {
        var w = NativeMethods.XDisplayWidth(_display, _screen);
        var h = NativeMethods.XDisplayHeight(_display, _screen);
        return new ScreenRect(string.Empty, 0, 0, w, h, false);
    }

    public void MoveMouse(int x, int y)
    {
        _ = NativeMethods.XTestFakeMotionEvent(_display, _screen, x, y, 0);
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
        if (msg.YDelta != 0)
        {
            var button = msg.YDelta > 0 ? 4u : 5u;
            var clicks = Math.Abs(msg.YDelta / 120);
            for (var i = 0; i < clicks; i++)
            {
                _ = NativeMethods.XTestFakeButtonEvent(_display, button, true, 0);
                _ = NativeMethods.XTestFakeButtonEvent(_display, button, false, 0);
            }
        }

        if (msg.XDelta != 0)
        {
            var button = msg.XDelta > 0 ? 7u : 6u;
            var clicks = Math.Abs(msg.XDelta / 120);
            for (var i = 0; i < clicks; i++)
            {
                _ = NativeMethods.XTestFakeButtonEvent(_display, button, true, 0);
                _ = NativeMethods.XTestFakeButtonEvent(_display, button, false, 0);
            }
        }

        _ = NativeMethods.XFlush(_display);
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
        if (XorgSpecialKeyMap.Reverse.TryGetValue(key, out var keysym))
            return keysym;

        // MISCELLANY keys: SpecialKey value encodes (keysym | 0x01000000), strip the flag
        var raw = (uint)key;
        if ((raw & 0xFF000000u) == 0x01000000u)
            return raw & 0x00FFFFFFu;

        return 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_display != nint.Zero)
            _ = NativeMethods.XCloseDisplay(_display);
    }
}
