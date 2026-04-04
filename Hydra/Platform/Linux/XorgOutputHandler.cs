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
    private int _scrollAccY;
    private int _scrollAccX;

    public XorgOutputHandler()
    {
        _ = NativeMethods.XInitThreads();
        _display = NativeMethods.XOpenDisplay(null);
        if (_display == nint.Zero)
            throw new InvalidOperationException("XOpenDisplay failed — is DISPLAY set?");

        _screen = NativeMethods.XDefaultScreen(_display);
        _rootWindow = NativeMethods.XDefaultRootWindow(_display);
    }

    public List<DetectedScreen> GetAllScreens() => XorgDisplayHelper.GetAllScreens(_display, _rootWindow);

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
        // x11 scroll: buttons 4=up, 5=down, 6=left, 7=right (each press = one 120-unit click).
        // accumulate remainders so sub-120 deltas are not silently dropped.
        _scrollAccY += msg.YDelta;
        _scrollAccX += msg.XDelta;
        InjectScrollAxis(4u, 5u, ref _scrollAccY);
        InjectScrollAxis(7u, 6u, ref _scrollAccX);
        _ = NativeMethods.XFlush(_display);
    }

    private void InjectScrollAxis(uint positiveButton, uint negativeButton, ref int accumulator)
    {
        var clicks = accumulator / 120;
        if (clicks == 0) return;
        accumulator -= clicks * 120;
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
        if (_display != nint.Zero)
            _ = NativeMethods.XCloseDisplay(_display);
    }
}
