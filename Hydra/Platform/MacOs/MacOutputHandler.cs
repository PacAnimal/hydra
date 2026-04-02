using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Relay;
using Hydra.Screen;

namespace Hydra.Platform.MacOs;

public sealed class MacOutputHandler : IPlatformOutput
{
    private double _mouseX;
    private double _mouseY;
    private MouseButton? _heldButton;

    public ScreenRect GetPrimaryScreenBounds()
    {
        var display = NativeMethods.CGMainDisplayID();
        var bounds = NativeMethods.CGDisplayBounds(display);
        return new ScreenRect(string.Empty, string.Empty, 0, 0, (int)bounds.Size.X, (int)bounds.Size.Y, IsLocal: true);
    }

    public List<DetectedScreen> GetAllScreens() => MacDisplayHelper.GetAllScreens();

    public void MoveMouse(int x, int y)
    {
        _mouseX = x;
        _mouseY = y;
        var pos = new CGPoint { X = x, Y = y };

        // post a real event so apps and OS features (dock, hot corners) see the movement.
        // use drag event type when a button is held, otherwise plain moved
        var (evType, btn) = _heldButton switch
        {
            MouseButton.Left => (NativeMethods.KCGEventLeftMouseDragged, 0),
            MouseButton.Right => (NativeMethods.KCGEventRightMouseDragged, 1),
            { } b => (NativeMethods.KCGEventOtherMouseDragged, (int)b - 1),
            null => (NativeMethods.KCGEventMouseMoved, 0),
        };

        var eventRef = NativeMethods.CGEventCreateMouseEvent(nint.Zero, evType, pos, btn);
        if (eventRef == nint.Zero) return;
        NativeMethods.CGEventPost(NativeMethods.KCGHidEventTap, eventRef);
        NativeMethods.CFRelease(eventRef);
    }

    public void InjectKey(KeyEventMessage msg)
    {
        var isDown = msg.Type == KeyEventType.KeyDown;

        if (msg.Character is { } ch)
            InjectUnicodeChar(ch, isDown);
        else if (msg.Key is { } key && MacSpecialKeyMap.Reverse.TryGetValue(key, out var vk))
            InjectVirtualKey((ushort)vk, isDown);
    }

    public void InjectMouseButton(MouseButtonMessage msg)
    {
        int mouseType;
        int mouseButton;

        switch (msg.Button)
        {
            case MouseButton.Left:
                mouseType = msg.IsPressed ? NativeMethods.KCGEventLeftMouseDown : NativeMethods.KCGEventLeftMouseUp;
                mouseButton = 0;
                break;
            case MouseButton.Right:
                mouseType = msg.IsPressed ? NativeMethods.KCGEventRightMouseDown : NativeMethods.KCGEventRightMouseUp;
                mouseButton = 1;
                break;
            default:
                mouseType = msg.IsPressed ? NativeMethods.KCGEventOtherMouseDown : NativeMethods.KCGEventOtherMouseUp;
                mouseButton = (int)msg.Button - 1;
                break;
        }

        if (msg.IsPressed) _heldButton = msg.Button;
        else if (_heldButton == msg.Button) _heldButton = null;

        var pos = new CGPoint { X = _mouseX, Y = _mouseY };
        var eventRef = NativeMethods.CGEventCreateMouseEvent(nint.Zero, mouseType, pos, mouseButton);
        if (eventRef == nint.Zero) return;

        if (msg.Button is not MouseButton.Left and not MouseButton.Right)
            NativeMethods.CGEventSetIntegerValueField(eventRef, NativeMethods.KCGMouseEventButtonNumber, mouseButton);

        NativeMethods.CGEventPost(NativeMethods.KCGHidEventTap, eventRef);
        NativeMethods.CFRelease(eventRef);
    }

    public void InjectMouseScroll(MouseScrollMessage msg)
    {
        if (msg.YDelta == 0 && msg.XDelta == 0) return;

        // kCGScrollEventUnitLine = 1; convert 120-unit deltas to line clicks
        var dy = msg.YDelta / 120;
        var dx = msg.XDelta / 120;

        var eventRef = NativeMethods.CGEventCreateScrollWheelEvent(nint.Zero, 1, 2, dy, dx);
        if (eventRef == nint.Zero) return;
        NativeMethods.CGEventPost(NativeMethods.KCGHidEventTap, eventRef);
        NativeMethods.CFRelease(eventRef);
    }

    private static unsafe void InjectUnicodeChar(char ch, bool isDown)
    {
        var eventRef = NativeMethods.CGEventCreateKeyboardEvent(nint.Zero, 0, isDown);
        if (eventRef == nint.Zero) return;
        var utf16 = (ushort)ch;
        NativeMethods.CGEventKeyboardSetUnicodeString(eventRef, 1, &utf16);
        NativeMethods.CGEventPost(NativeMethods.KCGHidEventTap, eventRef);
        NativeMethods.CFRelease(eventRef);
    }

    private static void InjectVirtualKey(ushort vk, bool isDown)
    {
        var eventRef = NativeMethods.CGEventCreateKeyboardEvent(nint.Zero, vk, isDown);
        if (eventRef == nint.Zero) return;
        NativeMethods.CGEventPost(NativeMethods.KCGHidEventTap, eventRef);
        NativeMethods.CFRelease(eventRef);
    }

    public void Dispose() { }
}
