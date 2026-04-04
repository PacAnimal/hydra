using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Relay;
using Hydra.Screen;

namespace Hydra.Platform.MacOs;

public sealed class MacOutputHandler : IPlatformOutput
{
    private double _mouseX;
    private double _mouseY;
    private readonly HashSet<MouseButton> _heldButtons = [];
    private int _scrollAccY;
    private int _scrollAccX;

    public List<DetectedScreen> GetAllScreens() => MacDisplayHelper.GetAllScreens();

    public void MoveMouse(int x, int y)
    {
        _mouseX = x;
        _mouseY = y;
        var pos = new CGPoint { X = x, Y = y };

        // post a real event so apps and OS features (dock, hot corners) see the movement.
        // use drag event type when a button is held, otherwise plain moved
        var (evType, btn) = (_heldButtons.Contains(MouseButton.Left), _heldButtons.Contains(MouseButton.Right), _heldButtons.Count > 0) switch
        {
            (true, _, _) => (NativeMethods.KCGEventLeftMouseDragged, 0),
            (_, true, _) => (NativeMethods.KCGEventRightMouseDragged, 1),
            (_, _, true) => (NativeMethods.KCGEventOtherMouseDragged, MouseButtonToCg(_heldButtons.Min())),
            _ => (NativeMethods.KCGEventMouseMoved, 0),
        };

        var eventRef = NativeMethods.CGEventCreateMouseEvent(nint.Zero, evType, pos, btn);
        if (eventRef == nint.Zero) return;
        NativeMethods.CGEventPost(NativeMethods.KCGHidEventTap, eventRef);
        NativeMethods.CFRelease(eventRef);
    }

    public void MoveMouseRelative(int dx, int dy)
    {
        _mouseX += dx;
        _mouseY += dy;
        var pos = new CGPoint { X = _mouseX, Y = _mouseY };

        var (evType, btn) = (_heldButtons.Contains(MouseButton.Left), _heldButtons.Contains(MouseButton.Right), _heldButtons.Count > 0) switch
        {
            (true, _, _) => (NativeMethods.KCGEventLeftMouseDragged, 0),
            (_, true, _) => (NativeMethods.KCGEventRightMouseDragged, 1),
            (_, _, true) => (NativeMethods.KCGEventOtherMouseDragged, MouseButtonToCg(_heldButtons.Min())),
            _ => (NativeMethods.KCGEventMouseMoved, 0),
        };

        var eventRef = NativeMethods.CGEventCreateMouseEvent(nint.Zero, evType, pos, btn);
        if (eventRef == nint.Zero) return;
        NativeMethods.CGEventSetIntegerValueField(eventRef, NativeMethods.KCGMouseEventDeltaX, dx);
        NativeMethods.CGEventSetIntegerValueField(eventRef, NativeMethods.KCGMouseEventDeltaY, dy);
        NativeMethods.CGEventPost(NativeMethods.KCGHidEventTap, eventRef);
        NativeMethods.CFRelease(eventRef);
    }

    public void InjectKey(KeyEventMessage msg)
    {
        var isDown = msg.Type == KeyEventType.KeyDown;

        if (msg.Character is { } ch)
            InjectUnicodeChar(ch, isDown);
        else if (msg.Key is { } key && MacSpecialKeyMap.Instance.Reverse.TryGetValue(key, out var vk))
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
                mouseButton = MouseButtonToCg(msg.Button);
                break;
        }

        if (msg.IsPressed) _heldButtons.Add(msg.Button);
        else _heldButtons.Remove(msg.Button);

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

        // accumulate into running totals to avoid losing sub-120 remainders
        _scrollAccY += msg.YDelta;
        _scrollAccX += msg.XDelta;

        // integer line count for line-based apps (may be zero for sub-line events)
        var yLines = _scrollAccY / 120;
        var xLines = _scrollAccX / 120;
        _scrollAccY -= yLines * 120;
        _scrollAccX -= xLines * 120;

        // kCGScrollEventUnitLine = 1
        var eventRef = NativeMethods.CGEventCreateScrollWheelEvent(nint.Zero, 1, 2, yLines, xLines);
        if (eventRef == nint.Zero) return;

        // also set pixel delta so pixel-aware apps (browsers, etc.) get smooth sub-line scroll
        NativeMethods.CGEventSetIntegerValueField(eventRef, NativeMethods.KCGScrollWheelEventPointDeltaAxis1, msg.YDelta);
        NativeMethods.CGEventSetIntegerValueField(eventRef, NativeMethods.KCGScrollWheelEventPointDeltaAxis2, msg.XDelta);

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

    // reverse of CgButtonToMouseButton (input side): 0=left, 1=right, 2=middle, 3+=extra
    private static int MouseButtonToCg(MouseButton button) => button switch
    {
        MouseButton.Left => 0,
        MouseButton.Right => 1,
        MouseButton.Middle => 2,
        MouseButton.Extra1 => 3,
        _ => 4,
    };

    public void Dispose() { }
}
