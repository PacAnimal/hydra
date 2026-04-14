using Cathedral.Utils;
using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Relay;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Linux;

public sealed class XorgOutputHandler : IPlatformOutput, ICursorVisibility
{
    private bool _cursorHidden;
    private readonly nint _display;
    private readonly int _screen;
    private readonly nint _rootWindow;
    private readonly Toggle _disposed = new();
    private readonly Queue<int> _unusedKeycodes = [];
    private readonly Dictionary<ulong, int> _tempBindings = [];
    private readonly ILogger<XorgOutputHandler> _log;

    public XorgOutputHandler(ILogger<XorgOutputHandler> log)
    {
        _log = log;
        _ = NativeMethods.XInitThreads();
        _display = NativeMethods.XOpenDisplay(null);
        if (_display == nint.Zero)
            throw new InvalidOperationException("XOpenDisplay failed — is DISPLAY set?");

        _screen = NativeMethods.XDefaultScreen(_display);
        _rootWindow = NativeMethods.XDefaultRootWindow(_display);
        // allow XTest events during active grabs (e.g. fullscreen games)
        _ = NativeMethods.XTestGrabControl(_display, true);
        FindUnusedKeycodes();
    }

    private unsafe void FindUnusedKeycodes()
    {
        _ = NativeMethods.XDisplayKeycodes(_display, out var minKeycode, out var maxKeycode);
        var count = maxKeycode - minKeycode + 1;
        var map = NativeMethods.XGetKeyboardMapping(_display, (uint)minKeycode, count, out var keysymsPerKeycode);
        if (map == nint.Zero) return;

        var keysyms = (ulong*)map;
        for (var i = 0; i < count; i++)
        {
            var allEmpty = true;
            for (var j = 0; j < keysymsPerKeycode; j++)
            {
                if (keysyms[i * keysymsPerKeycode + j] != 0) { allEmpty = false; break; }
            }
            if (allEmpty)
                _unusedKeycodes.Enqueue(minKeycode + i);
        }

        _ = NativeMethods.XFree(map);
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
            var keysym = ch <= '\xFF' ? (ulong)ch : 0x01000000u | ch;
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
        if (keycode != 0)
        {
            _ = NativeMethods.XTestFakeKeyEvent(_display, keycode, isDown, 0);
            _ = NativeMethods.XFlush(_display);
            return;
        }

        // keysym has no keycode in the slave's layout — temporarily bind it to an unused keycode
        if (isDown)
        {
            if (_unusedKeycodes.Count == 0)
            {
                _log.LogWarning("No unused keycodes available — dropping keysym 0x{Keysym:X}", keysym);
                return;
            }
            // clear any existing binding for this keysym before creating a new one
            if (_tempBindings.TryGetValue(keysym, out var stale))
            {
                _ = NativeMethods.XChangeKeyboardMapping(_display, stale, 1, [0UL], 1);
                _ = NativeMethods.XSync(_display, false);
                _unusedKeycodes.Enqueue(stale);
            }
            var tempKeycode = _unusedKeycodes.Dequeue();
            _ = NativeMethods.XChangeKeyboardMapping(_display, tempKeycode, 1, [keysym], 1);
            _ = NativeMethods.XSync(_display, false);
            _ = NativeMethods.XTestFakeKeyEvent(_display, (uint)tempKeycode, true, 0);
            _ = NativeMethods.XFlush(_display);
            _tempBindings[keysym] = tempKeycode;
        }
        else
        {
            if (!_tempBindings.TryGetValue(keysym, out var tempKeycode)) return;
            _ = NativeMethods.XTestFakeKeyEvent(_display, (uint)tempKeycode, false, 0);
            _ = NativeMethods.XChangeKeyboardMapping(_display, tempKeycode, 1, [0UL], 1);
            _ = NativeMethods.XSync(_display, false);
            _unusedKeycodes.Enqueue(tempKeycode);
            _tempBindings.Remove(keysym);
        }
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
        if (!_disposed.TrySet()) return;
        if (_display != nint.Zero)
        {
            foreach (var tempKeycode in _tempBindings.Values)
                _ = NativeMethods.XChangeKeyboardMapping(_display, tempKeycode, 1, [0UL], 1);
            if (_tempBindings.Count > 0)
                _ = NativeMethods.XSync(_display, false);
            if (_cursorHidden)
                NativeMethods.XFixesShowCursor(_display, _rootWindow);
            _ = NativeMethods.XCloseDisplay(_display);
        }
    }
}
