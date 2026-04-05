using System.Runtime.InteropServices;
using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Platform;
using Hydra.Relay;
using Hydra.Screen;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.MacOs;

public sealed class MacOutputHandler : IPlatformOutput, ICursorVisibility
{
    private readonly ILogger<MacOutputHandler> _log;

    private double _mouseX;
    private double _mouseY;
    private readonly HashSet<MouseButton> _heldButtons = [];
    private ScrollAccumulator _scrollAccY;
    private ScrollAccumulator _scrollAccX;
    private readonly uint _display = NativeMethods.CGMainDisplayID();
    private bool _cursorHidden;

    // accumulated modifier state — updated on each modifier keydown/up
    private ulong _modifierFlags;        // generic CGEventFlag masks (e.g. kCGEventFlagMaskCommand)
    private uint _deviceModifierFlags;   // device-dependent NX masks (e.g. NX_DEVICELCMDKEYMASK)

    // IOKit HID driver connection (deskflow's getEventDriver() pattern)
    private uint _hidConnection;

    // ReSharper disable once ConvertToPrimaryConstructor
#pragma warning disable IDE0290
    public MacOutputHandler(ILogger<MacOutputHandler> log)
    {
        _log = log;
    }
#pragma warning restore IDE0290

    // mach_task_self_ is a global variable in libSystem — read it by dereferencing the export address,
    // NOT by calling it as a function (calling a data segment address as code causes a crash).
    private static readonly uint _machTaskSelf = (uint)Marshal.ReadInt32(
        NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libSystem.B.dylib"), "mach_task_self_"));

    // kCFBooleanTrue for CGSSetConnectionProperty("SetsCursorInBackground")
    private static readonly nint _cfBooleanTrue = Marshal.ReadIntPtr(
        NativeLibrary.GetExport(
            NativeLibrary.Load("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation"),
            "kCFBooleanTrue"));

    // combined session state source — used for mouse events and CGEvent fallback paths
    private static readonly nint _eventSource = NativeMethods.CGEventSourceCreate(NativeMethods.KCGEventSourceStateCombinedSessionState);

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

        var eventRef = NativeMethods.CGEventCreateMouseEvent(_eventSource, move.EventType, pos, move.Button);
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

        var eventRef = NativeMethods.CGEventCreateMouseEvent(_eventSource, move.EventType, pos, move.Button);
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

        if (msg.Key is { } key && key.IsModifier() && MacSpecialKeyMap.Instance.Reverse.TryGetValue(key, out var modVk))
        {
            // update accumulated modifier state, then inject via IOHIDPostEvent (NX_FLAGSCHANGED) to update
            // the system-wide HID modifier state read by [NSEvent modifierFlags] class method (what Chromium queries).
            // falls back to CGEventPost when IOKit unavailable.
            var (generic, device) = VkToModifierMasks((ushort)modVk);
            if (isDown) { _modifierFlags |= generic; _deviceModifierFlags |= device; }
            else { _modifierFlags &= ~generic; _deviceModifierFlags &= ~device; }

            if (!PostHidModifier((ushort)modVk))
                PostFlagsChanged((ushort)modVk, isDown);
        }
        else if (msg.Character is { } ch)
        {
            // deskflow approach: inject via IOHIDPostEvent (NX_KEYDOWN/UP, flags=0) so the system uses the
            // current HID modifier state for shortcut resolution. fall back to CGEventPost with unicode
            // string for chars with no VK mapping (foreign/composed characters).
            if (_charToVk.TryGetValue(char.ToLowerInvariant(ch), out var charVk))
            {
                if (!PostHidKey(charVk, isDown))
                    PostCGKey(charVk, isDown, flags);
            }
            else
            {
                InjectCharacter(ch, isDown, flags);
            }
        }
        else if (msg.Key is { } key2 && MacSpecialKeyMap.Instance.Reverse.TryGetValue(key2, out var vk))
        {
            // non-modifier special key: same IOHIDPostEvent-first approach
            if (!PostHidKey((ushort)vk, isDown))
                PostCGKey((ushort)vk, isDown, flags);
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
        var eventRef = NativeMethods.CGEventCreateMouseEvent(_eventSource, mouseType, pos, mouseButton);
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
        var eventRef = NativeMethods.CGEventCreateScrollWheelEvent(_eventSource, 1, 2, yLines, xLines);
        if (eventRef == nint.Zero) return;

        // also set pixel delta so pixel-aware apps (browsers, etc.) get smooth sub-line scroll
        NativeMethods.CGEventSetIntegerValueField(eventRef, NativeMethods.KCGScrollWheelEventPointDeltaAxis1, msg.YDelta);
        NativeMethods.CGEventSetIntegerValueField(eventRef, NativeMethods.KCGScrollWheelEventPointDeltaAxis2, msg.XDelta);

        NativeMethods.CGEventPost(NativeMethods.KCGHidEventTap, eventRef);
        NativeMethods.CFRelease(eventRef);
    }

    // post a CG keyboard event — used for non-modifier special keys and as fallback for modifiers.
    private static void PostCGKey(ushort vk, bool isDown, ulong flags)
    {
        var eventRef = NativeMethods.CGEventCreateKeyboardEvent(_eventSource, vk, isDown);
        if (eventRef == nint.Zero) return;
        NativeMethods.CGEventSetFlags(eventRef, flags);
        NativeMethods.CGEventPost(NativeMethods.KCGHidEventTap, eventRef);
        NativeMethods.CFRelease(eventRef);
    }

    // post NX_FLAGSCHANGED via IOHIDPostEvent — updates system-wide modifier state so
    // [NSEvent modifierFlags] (class method) reflects held keys. mirrors deskflow's postHIDVirtualKey().
    private bool PostHidModifier(ushort vk)
    {
        var conn = GetHidConnection();
        if (conn == 0) return false;

        var eventData = new NXEventData { KeyCode = vk };
        var eventFlags = (uint)_modifierFlags | _deviceModifierFlags;
        var kr = NativeMethods.IOHIDPostEvent(conn, NativeMethods.NxFlagsChanged, default, in eventData,
            NativeMethods.KNxEventDataVersion, eventFlags, 1); // 1 = kIOHIDSetGlobalEventFlags
        if (kr != 0)
            _log.LogWarning("IOHIDPostEvent(NX_FLAGSCHANGED) failed: kr={Kr} vk=0x{Vk:x2}", kr, vk);
        return kr == 0;
    }

    // post NX_KEYDOWN/NX_KEYUP via IOHIDPostEvent — flags=0 so system uses current HID modifier state.
    // mirrors deskflow's postHIDVirtualKey() for non-modifier keys.
    private bool PostHidKey(ushort vk, bool isDown)
    {
        var conn = GetHidConnection();
        if (conn == 0) return false;

        var eventData = new NXEventData { KeyCode = vk };
        var eventType = isDown ? NativeMethods.NxKeyDown : NativeMethods.NxKeyUp;
        var kr = NativeMethods.IOHIDPostEvent(conn, eventType, default, in eventData,
            NativeMethods.KNxEventDataVersion, 0, 0);
        if (kr != 0)
            _log.LogWarning("IOHIDPostEvent(NX_KEY{Dir}) failed: kr={Kr} vk=0x{Vk:x2}", isDown ? "DOWN" : "UP", kr, vk);
        return kr == 0;
    }

    // lazy-init IOKit HID driver connection. mirrors deskflow's getEventDriver().
    private uint GetHidConnection()
    {
        if (_hidConnection != 0) return _hidConnection;

        NativeMethods.IOMasterPort(0, out var masterPort);
        var matching = NativeMethods.IOServiceMatching("IOHIDSystem");
        if (matching == nint.Zero)
        {
            _log.LogWarning("IOServiceMatching(IOHIDSystem) returned null — IOHIDPostEvent unavailable");
            return 0;
        }

        if (NativeMethods.IOServiceGetMatchingServices(masterPort, matching, out var iter) != 0)
        {
            _log.LogWarning("IOServiceGetMatchingServices failed — IOHIDPostEvent unavailable");
            return 0;
        }

        var service = NativeMethods.IOIteratorNext(iter);
        _ = NativeMethods.IOObjectRelease(iter);
        if (service == 0)
        {
            _log.LogWarning("IOIteratorNext returned no IOHIDSystem service — IOHIDPostEvent unavailable");
            return 0;
        }

        var kr = NativeMethods.IOServiceOpen(service, _machTaskSelf, NativeMethods.KIoHidParamConnectType, out var conn);
        _ = NativeMethods.IOObjectRelease(service);
        if (kr != 0)
        {
            _log.LogWarning("IOServiceOpen failed: kr={Kr} — IOHIDPostEvent unavailable", kr);
            return 0;
        }

        _log.LogInformation("IOKit HID connection established (conn={Conn})", conn);
        _hidConnection = conn;
        return _hidConnection;
    }

    // fallback: post kCGEventFlagsChanged via CGEventPost when IOHIDPostEvent unavailable.
    // mirrors deskflow's postKeyboardKey() fallback path.
    private void PostFlagsChanged(ushort vk, bool isDown)
    {
        var eventRef = NativeMethods.CGEventCreateKeyboardEvent(_eventSource, vk, isDown);
        if (eventRef == nint.Zero) return;
        NativeMethods.CGEventSetType(eventRef, NativeMethods.KCGEventFlagsChanged);
        NativeMethods.CGEventSetFlags(eventRef, _modifierFlags);
        NativeMethods.CGEventPost(NativeMethods.KCGHidEventTap, eventRef);
        NativeMethods.CFRelease(eventRef);
    }

    // maps a macOS virtual key code to (generic CGEventFlag mask, device-dependent NX mask).
    private static (ulong generic, uint device) VkToModifierMasks(ushort vk) => (ulong)vk switch
    {
        MacVirtualKey.Command or MacVirtualKey.RightCommand =>
            (NativeMethods.KCGEventFlagMaskCommand, NativeMethods.NxDeviceLCmdKeyMask),
        MacVirtualKey.Shift or MacVirtualKey.RightShift =>
            (NativeMethods.KCGEventFlagMaskShift, NativeMethods.NxDeviceLShiftKeyMask),
        MacVirtualKey.Control or MacVirtualKey.RightControl =>
            (NativeMethods.KCGEventFlagMaskControl, NativeMethods.NxDeviceLCtlKeyMask),
        MacVirtualKey.Option or MacVirtualKey.RightOption =>
            (NativeMethods.KCGEventFlagMaskAlternate, NativeMethods.NxDeviceLAltKeyMask),
        MacVirtualKey.CapsLock => (NativeMethods.KCGEventFlagMaskAlphaShift, 0),
        _ => (0, 0)
    };

    // fallback character injection for chars with no VK mapping (foreign/composed chars).
    // vk=0 with explicit unicode string — only reached when _charToVk has no entry for the char.
    private static unsafe void InjectCharacter(char ch, bool isDown, ulong flags)
    {
        var eventRef = NativeMethods.CGEventCreateKeyboardEvent(_eventSource, 0, isDown);
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

    // reverse of MacKeyResolver.MapModifiers(): KeyModifiers → CGEventFlags.
    // note: KeyModifiers.NumLock is NOT mapped to kCGEventFlagMaskNumericPad here.
    // on Linux, NumLock is a system-wide lock state present on all key events.
    // on macOS, kCGEventFlagMaskNumericPad means "this key is a numpad key" — a per-key identity.
    // injecting it on regular keys (e.g. 'a') causes Chromium-based apps to reject the event.
    internal static ulong MapModifiersToFlags(KeyModifiers mods)
    {
        ulong flags = 0;
        if ((mods & KeyModifiers.Shift) != 0) flags |= NativeMethods.KCGEventFlagMaskShift;
        if ((mods & KeyModifiers.Control) != 0) flags |= NativeMethods.KCGEventFlagMaskControl;
        if ((mods & KeyModifiers.Alt) != 0) flags |= NativeMethods.KCGEventFlagMaskAlternate;
        if ((mods & KeyModifiers.Super) != 0) flags |= NativeMethods.KCGEventFlagMaskCommand;
        if ((mods & KeyModifiers.CapsLock) != 0) flags |= NativeMethods.KCGEventFlagMaskAlphaShift;
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
        if (_hidConnection != 0)
            _ = NativeMethods.IOObjectRelease(_hidConnection);
    }

    private record MoveEvent(int EventType, int Button);
}
