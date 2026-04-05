using System.Runtime.InteropServices;
using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Platform;
using Hydra.Relay;
using Hydra.Screen;

namespace Hydra.Platform.MacOs;

public sealed class MacOutputHandler : IPlatformOutput, ICursorVisibility
{
    private double _mouseX;
    private double _mouseY;
    private readonly HashSet<MouseButton> _heldButtons = [];
    private ScrollAccumulator _scrollAccY;
    private ScrollAccumulator _scrollAccX;
    private readonly uint _display = NativeMethods.CGMainDisplayID();
    private bool _cursorHidden;

    // kCFBooleanTrue for CGSSetConnectionProperty("SetsCursorInBackground")
    private static readonly nint _cfBooleanTrue = Marshal.ReadIntPtr(
        NativeLibrary.GetExport(
            NativeLibrary.Load("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation"),
            "kCFBooleanTrue"));

    // char produced by each vk code (no modifiers) — used to find the correct vk for character injection
    private static readonly Dictionary<char, ushort> _charToVk = BuildCharToVkMap();


    public void MoveMouse(int x, int y)
    {
        _mouseX = x;
        _mouseY = y;
        var pos = new CGPoint { X = x, Y = y };

        // post a real event so apps and OS features (dock, hot corners) see the movement.
        // use drag event type when a button is held, otherwise plain moved
        var move = GetMoveEventType();

        var eventRef = NativeMethods.CGEventCreateMouseEvent(nint.Zero, move.EventType, pos, move.Button);
        if (eventRef == nint.Zero) return;
        NativeMethods.CGEventPost(NativeMethods.KCGHidEventTap, eventRef);
        NativeMethods.CFRelease(eventRef);
    }

    public void MoveMouseRelative(int dx, int dy)
    {
        // read actual cursor position before moving — avoids unbounded drift of our own tracking,
        // and keeps _mouseX/_mouseY accurate for InjectMouseButton (matches barrier/deskflow approach)
        var posQuery = NativeMethods.CGEventCreate(nint.Zero);
        if (posQuery != nint.Zero)
        {
            var cur = NativeMethods.CGEventGetLocation(posQuery);
            _mouseX = cur.X + dx;
            _mouseY = cur.Y + dy;
            NativeMethods.CFRelease(posQuery);
        }
        else
        {
            _mouseX += dx;
            _mouseY += dy;
        }

        var pos = new CGPoint { X = _mouseX, Y = _mouseY };

        var move = GetMoveEventType();

        var eventRef = NativeMethods.CGEventCreateMouseEvent(nint.Zero, move.EventType, pos, move.Button);
        if (eventRef == nint.Zero) return;
        // set integer AND double delta fields — some 3D apps/games read the double variant (barrier comment)
        NativeMethods.CGEventSetIntegerValueField(eventRef, NativeMethods.KCGMouseEventDeltaX, dx);
        NativeMethods.CGEventSetIntegerValueField(eventRef, NativeMethods.KCGMouseEventDeltaY, dy);
        NativeMethods.CGEventSetDoubleValueField(eventRef, NativeMethods.KCGMouseEventDeltaX, dx);
        NativeMethods.CGEventSetDoubleValueField(eventRef, NativeMethods.KCGMouseEventDeltaY, dy);
        NativeMethods.CGEventPost(NativeMethods.KCGHidEventTap, eventRef);
        NativeMethods.CFRelease(eventRef);
    }

    public void InjectKey(KeyEventMessage msg)
    {
        var isDown = msg.Type == KeyEventType.KeyDown;
        var flags = MapModifiersToFlags(msg.Modifiers);

        if (msg.Character is { } ch)
        {
            InjectCharacter(ch, isDown, flags);
        }
        else if (msg.Key is { } key && MacSpecialKeyMap.Instance.Reverse.TryGetValue(key, out var vk))
        {
            var eventRef = NativeMethods.CGEventCreateKeyboardEvent(nint.Zero, (ushort)vk, isDown);
            if (eventRef == nint.Zero) return;
            NativeMethods.CGEventSetFlags(eventRef, flags);
            NativeMethods.CGEventPost(NativeMethods.KCGHidEventTap, eventRef);
            NativeMethods.CFRelease(eventRef);
        }
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
        // integer line count for line-based apps (may be zero for sub-line events)
        var yLines = _scrollAccY.Add(msg.YDelta);
        var xLines = _scrollAccX.Add(msg.XDelta);

        // kCGScrollEventUnitLine = 1
        var eventRef = NativeMethods.CGEventCreateScrollWheelEvent(nint.Zero, 1, 2, yLines, xLines);
        if (eventRef == nint.Zero) return;

        // also set pixel delta so pixel-aware apps (browsers, etc.) get smooth sub-line scroll
        NativeMethods.CGEventSetIntegerValueField(eventRef, NativeMethods.KCGScrollWheelEventPointDeltaAxis1, msg.YDelta);
        NativeMethods.CGEventSetIntegerValueField(eventRef, NativeMethods.KCGScrollWheelEventPointDeltaAxis2, msg.XDelta);

        NativeMethods.CGEventPost(NativeMethods.KCGHidEventTap, eventRef);
        NativeMethods.CFRelease(eventRef);
    }

    // inject a character key event.
    // look up the vk code for the base character so macOS shortcut processing sees the right key.
    // fall back to vk=0 with unicode string for chars with no mapping (foreign/composed chars).
    private static unsafe void InjectCharacter(char ch, bool isDown, ulong flags)
    {
        var vk = _charToVk.TryGetValue(char.ToLowerInvariant(ch), out var mappedVk) ? mappedVk : (ushort)0;
        var eventRef = NativeMethods.CGEventCreateKeyboardEvent(nint.Zero, vk, isDown);
        if (eventRef == nint.Zero) return;
        NativeMethods.CGEventSetFlags(eventRef, flags);
        var utf16 = (ushort)ch;
        NativeMethods.CGEventKeyboardSetUnicodeString(eventRef, 1, &utf16);
        NativeMethods.CGEventPost(NativeMethods.KCGHidEventTap, eventRef);
        NativeMethods.CFRelease(eventRef);
    }

    // build reverse map: unshifted character → vk code, from current keyboard layout.
    // iterates vk 0–127 via UCKeyTranslate with no modifiers. mirrors MacKeyResolver.ResolveCharacter().
    private static unsafe Dictionary<char, ushort> BuildCharToVkMap()
    {
        var map = new Dictionary<char, ushort>();

        var layoutSource = NativeMethods.TISCopyCurrentKeyboardLayoutInputSource();
        if (layoutSource == nint.Zero) return map;

        try
        {
            var layoutData = NativeMethods.TISGetInputSourceProperty(layoutSource, LoadTisPropertyKey());
            if (layoutData == nint.Zero) return map;

            var layoutPtr = NativeMethods.CFDataGetBytePtr(layoutData);
            if (layoutPtr == nint.Zero) return map;

            var kbdType = NativeMethods.LMGetKbdType();
            uint deadKeyState = 0;
            ushort* chars = stackalloc ushort[2];

            for (ushort vk = 0; vk < 128; vk++)
            {
                deadKeyState = 0;
                var status = NativeMethods.UCKeyTranslate(
                    layoutPtr,
                    vk,
                    NativeMethods.KUCKeyActionDown,
                    0, // no modifiers
                    kbdType,
                    1, // kUCKeyTranslateNoDeadKeysBit — skip dead key composition
                    ref deadKeyState,
                    2,
                    out var count,
                    chars);

                if (status != 0 || count == 0) continue;

                var ch = (char)chars[0];
                // only map printable chars, skip control chars; first mapping wins
                if (ch >= 0x20 && !map.ContainsKey(ch))
                    map[ch] = vk;
            }
        }
        finally
        {
            NativeMethods.CFRelease(layoutSource);
        }

        return map;
    }

    private static nint LoadTisPropertyKey()
    {
        var carbon = System.Runtime.InteropServices.NativeLibrary.Load(
            "/System/Library/Frameworks/Carbon.framework/Carbon");
        return System.Runtime.InteropServices.Marshal.ReadIntPtr(
            System.Runtime.InteropServices.NativeLibrary.GetExport(carbon, "kTISPropertyUnicodeKeyLayoutData"));
    }

    // reverse of MacKeyResolver.MapModifiers(): KeyModifiers → CGEventFlags
    private static ulong MapModifiersToFlags(KeyModifiers mods)
    {
        ulong flags = 0;
        if ((mods & KeyModifiers.Shift) != 0) flags |= NativeMethods.KCGEventFlagMaskShift;
        if ((mods & KeyModifiers.Control) != 0) flags |= NativeMethods.KCGEventFlagMaskControl;
        if ((mods & KeyModifiers.Alt) != 0) flags |= NativeMethods.KCGEventFlagMaskAlternate;
        if ((mods & KeyModifiers.Super) != 0) flags |= NativeMethods.KCGEventFlagMaskCommand;
        if ((mods & KeyModifiers.CapsLock) != 0) flags |= NativeMethods.KCGEventFlagMaskAlphaShift;
        if ((mods & KeyModifiers.NumLock) != 0) flags |= NativeMethods.KCGEventFlagMaskNumericPad;
        return flags;
    }

    // drag event type when a button is held, otherwise plain moved
    private MoveEvent GetMoveEventType() =>
        (_heldButtons.Contains(MouseButton.Left), _heldButtons.Contains(MouseButton.Right), _heldButtons.Count > 0) switch
        {
            (true, _, _) => new(NativeMethods.KCGEventLeftMouseDragged, 0),
            (_, true, _) => new(NativeMethods.KCGEventRightMouseDragged, 1),
            (_, _, true) => new(NativeMethods.KCGEventOtherMouseDragged, MouseButtonToCg(_heldButtons.Min())),
            _ => new(NativeMethods.KCGEventMouseMoved, 0),
        };

    // reverse of CgButtonToMouseButton (input side): 0=left, 1=right, 2=middle, 3+=extra
    private static int MouseButtonToCg(MouseButton button) => button switch
    {
        MouseButton.Left => 0,
        MouseButton.Right => 1,
        MouseButton.Middle => 2,
        MouseButton.Extra1 => 3,
        _ => 4,
    };

    public void HideCursor()
    {
        if (_cursorHidden) return;
        // allow cursor manipulation from background (private CGS API — matches master + synergy)
        var cid = NativeMethods.CGSMainConnectionID();
        var key = NativeMethods.CFStringCreateWithCString(nint.Zero, "SetsCursorInBackground", NativeMethods.KCFStringEncodingUtf8);
        _ = NativeMethods.CGSSetConnectionProperty(cid, cid, key, _cfBooleanTrue);
        NativeMethods.CFRelease(key);
        _ = NativeMethods.CGDisplayHideCursor(_display);
        _cursorHidden = true;
    }

    public void ShowCursor()
    {
        if (!_cursorHidden) return;
        _ = NativeMethods.CGDisplayShowCursor(_display);
        _cursorHidden = false;
    }

    public CursorPosition GetCursorPosition()
    {
        var evt = NativeMethods.CGEventCreate(nint.Zero);
        if (evt == nint.Zero) return new CursorPosition(0, 0);
        var pos = NativeMethods.CGEventGetLocation(evt);
        NativeMethods.CFRelease(evt);
        return new CursorPosition((int)pos.X, (int)pos.Y);
    }

    public void Dispose()
    {
        if (_cursorHidden)
            _ = NativeMethods.CGDisplayShowCursor(_display);
    }

    private record MoveEvent(int EventType, int Button);
}
