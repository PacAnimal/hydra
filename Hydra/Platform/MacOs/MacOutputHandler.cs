using System.Runtime.InteropServices;
using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Relay;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.MacOs;

public sealed class MacOutputHandler : IPlatformOutput, ICursorVisibility
{
    private readonly ILogger<MacOutputHandler> _log;

    private double _mouseX;
    private double _mouseY;
    private readonly HashSet<MouseButton> _heldButtons = [];
    private readonly uint _display = NativeMethods.CGMainDisplayID();

    // click state tracking for kCGMouseEventClickState (mirrors input-leap OSXScreen logic)
    private int _clickState = 1;
    private long _lastClickTimeMs;
    private double _lastSingleClickX;
    private double _lastSingleClickY;
    private bool _cursorHidden;
    private bool _mousePositionInitialized;

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
    private static readonly uint MachTaskSelf = (uint)Marshal.ReadInt32(
        NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libSystem.B.dylib"), "mach_task_self_"));

    // combined session state source — used only for keyboard CGEvent fallback paths.
    // mouse events use nint.Zero (null source) so system UI does not filter them as synthetic.
    private static readonly nint EventSource = NativeMethods.CGEventSourceCreate(NativeMethods.KCGEventSourceStateCombinedSessionState);

    private static readonly double DoubleClickMaxDist = Math.Sqrt(2) + 0.0001;
    private static readonly double DoubleClickIntervalMs = GetDoubleClickIntervalMs();

    // char produced by each vk code (no modifiers) — used to find the correct vk for character injection
    private static readonly Dictionary<char, ushort> CharToVk = BuildCharToVkMap();

    public void MoveMouse(int x, int y)
    {
        var dx = _mousePositionInitialized ? x - _mouseX : 0.0;
        var dy = _mousePositionInitialized ? y - _mouseY : 0.0;
        _mouseX = x;
        _mouseY = y;
        _mousePositionInitialized = true;
        var pos = new CGPoint { X = x, Y = y };

        // post a real event so apps and OS features (dock, hot corners) see the movement.
        // use drag event type when a button is held, otherwise plain moved
        var move = GetMoveEventType();

        var eventRef = NativeMethods.CGEventCreateMouseEvent(nint.Zero, move.EventType, pos, move.Button);
        if (eventRef == nint.Zero) return;
        NativeMethods.CGEventSetIntegerValueField(eventRef, NativeMethods.KCGMouseEventClickState, _clickState);
        NativeMethods.CGEventSetFlags(eventRef, _modifierFlags);
        // delta fields needed for drag operations (e.g. screenshot overlay resize)
        NativeMethods.CGEventSetIntegerValueField(eventRef, NativeMethods.KCGMouseEventDeltaX, (long)dx);
        NativeMethods.CGEventSetIntegerValueField(eventRef, NativeMethods.KCGMouseEventDeltaY, (long)dy);
        NativeMethods.CGEventSetDoubleValueField(eventRef, NativeMethods.KCGMouseEventDeltaX, dx);
        NativeMethods.CGEventSetDoubleValueField(eventRef, NativeMethods.KCGMouseEventDeltaY, dy);
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
        NativeMethods.CGEventSetIntegerValueField(eventRef, NativeMethods.KCGMouseEventClickState, _clickState);
        NativeMethods.CGEventSetFlags(eventRef, _modifierFlags);
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
            if (CharToVk.TryGetValue(char.ToLowerInvariant(ch), out var charVk))
            {
                if (!PostHidKey(charVk, isDown))
                    PostCgKey(charVk, isDown, flags);
            }
            else
            {
                InjectCharacter(ch, isDown, flags);
            }
        }
        else if (msg.Key is { } key2)
        {
            // media keys require NX_SYSDEFINED injection via NSEvent — regular NX_KEYDOWN with the VK
            // produces wrong results (volume VKs hit wrong keys in the regular keycode space).
            var nxType = GetNxMediaKeyType(key2);
            if (nxType >= 0)
                PostNsMediaKey((uint)nxType, isDown);
            else if (MacSpecialKeyMap.Instance.Reverse.TryGetValue(key2, out var vk))
            {
                // non-modifier special key: IOHIDPostEvent-first, CGEvent fallback
                if (!PostHidKey((ushort)vk, isDown))
                    PostCgKey((ushort)vk, isDown, flags);
            }
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

        UpdateClickState(msg.IsPressed);

        var pos = new CGPoint { X = _mouseX, Y = _mouseY };
        var eventRef = NativeMethods.CGEventCreateMouseEvent(nint.Zero, mouseType, pos, mouseButton);
        if (eventRef == nint.Zero) return;

        if (msg.Button is not MouseButton.Left and not MouseButton.Right)
            NativeMethods.CGEventSetIntegerValueField(eventRef, NativeMethods.KCGMouseEventButtonNumber, mouseButton);

        NativeMethods.CGEventSetIntegerValueField(eventRef, NativeMethods.KCGMouseEventClickState, _clickState);
        NativeMethods.CGEventSetFlags(eventRef, _modifierFlags);

        NativeMethods.CGEventPost(NativeMethods.KCGHidEventTap, eventRef);
        NativeMethods.CFRelease(eventRef);
    }

    public void InjectMouseScroll(MouseScrollMessage msg)
    {
        if (msg.YDelta == 0 && msg.XDelta == 0) return;

        // wire format: 120 = 1 line. convert to integer line counts for the event constructor.
        // kCGScrollEventUnitLine = 1
        var eventRef = NativeMethods.CGEventCreateScrollWheelEvent(nint.Zero, 1, 2, msg.YDelta / 120, msg.XDelta / 120);
        if (eventRef == nint.Zero) return;

        // set 16.16 fixed-point line deltas for sub-line precision (reverses input handler's fpDelta * 120 >> 16)
        NativeMethods.CGEventSetIntegerValueField(eventRef, NativeMethods.KCGScrollWheelEventFixedPtDeltaAxis1, (long)msg.YDelta * 65536 / 120);
        NativeMethods.CGEventSetIntegerValueField(eventRef, NativeMethods.KCGScrollWheelEventFixedPtDeltaAxis2, (long)msg.XDelta * 65536 / 120);

        NativeMethods.CGEventPost(NativeMethods.KCGHidEventTap, eventRef);
        NativeMethods.CFRelease(eventRef);
    }

    // post a CG keyboard event — used for non-modifier special keys and as fallback for modifiers.
    private static void PostCgKey(ushort vk, bool isDown, ulong flags)
    {
        var eventRef = NativeMethods.CGEventCreateKeyboardEvent(EventSource, vk, isDown);
        if (eventRef == nint.Zero) return;
        NativeMethods.CGEventSetFlags(eventRef, flags | FnFlagForVk(vk));
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

    // post NX_KEYDOWN/NX_KEYUP via IOHIDPostEvent — flags carry the Fn bit for fn-row keys.
    // mirrors deskflow's postHIDVirtualKey() for non-modifier keys.
    private bool PostHidKey(ushort vk, bool isDown)
    {
        var conn = GetHidConnection();
        if (conn == 0) return false;

        var eventData = new NXEventData { KeyCode = vk };
        var eventType = isDown ? NativeMethods.NxKeyDown : NativeMethods.NxKeyUp;
        var eventFlags = (uint)FnFlagForVk(vk);
        var kr = NativeMethods.IOHIDPostEvent(conn, eventType, default, in eventData,
            NativeMethods.KNxEventDataVersion, eventFlags, 0);
        if (kr != 0)
            _log.LogWarning("IOHIDPostEvent(NX_KEY{Dir}) failed: kr={Kr} vk=0x{Vk:x2}", isDown ? "DOWN" : "UP", kr, vk);
        return kr == 0;
    }

    // inject a media key via [NSEvent otherEventWithType:NSSystemDefined subtype:8 ...] → CGEventPost.
    // mirrors barrier/deskflow's fakeNativeMediaKey(): NX_SYSDEFINED is the only reliable path for
    // volume, brightness, eject, play/next/prev — regular NX_KEYDOWN with the VK code misroutes.
    private static void PostNsMediaKey(uint keyType, bool isDown)
    {
        NativeMethods.EnsureAppKitLoaded();
        var cls = NativeMethods.objc_getClass("NSEvent");
        var sel = NativeMethods.sel_registerName("otherEventWithType:location:modifierFlags:timestamp:windowNumber:context:subtype:data1:data2:");
        var selCgEvent = NativeMethods.sel_registerName("CGEvent");

        // data1: high 16 bits = NX_KEYTYPE, bits 8–15 = 0x0a (down) or 0x0b (up)
        var data1 = (nint)((keyType << 16) | (isDown ? 0x0a00u : 0x0b00u));

        var nsEvent = NativeMethods.objc_msgSend_NSEvent_otherEvent(
            cls, sel,
            14,       // NSSystemDefined
            default,  // NSPoint(0, 0)
            0xa00,    // modifierFlags (empirical — matches barrier/deskflow)
            0,        // timestamp
            0,        // windowNumber
            0,        // context = nil
            8,        // subtype = NX_SUBTYPE_AUX_CONTROL_BUTTONS
            data1,
            -1);      // data2

        if (nsEvent == nint.Zero) return;
        var cgEventRef = NativeMethods.objc_msgSend_noarg(nsEvent, selCgEvent);
        if (cgEventRef == nint.Zero) return;
        NativeMethods.CGEventPost(NativeMethods.KCGHidEventTap, cgEventRef);
    }

    // maps media SpecialKeys to NX_KEYTYPE_* constants (ev_keymap.h); returns -1 for non-media keys.
    private static int GetNxMediaKeyType(SpecialKey key) => key switch
    {
        SpecialKey.AudioVolumeUp => (int)NativeMethods.NXKeytypeSoundUp,
        SpecialKey.AudioVolumeDown => (int)NativeMethods.NXKeytypeSoundDown,
        SpecialKey.AudioMute => (int)NativeMethods.NXKeytypeMute,
        SpecialKey.AudioPlay => (int)NativeMethods.NXKeytypePlay,
        SpecialKey.AudioNext => (int)NativeMethods.NXKeytypeNext,
        SpecialKey.AudioPrev => (int)NativeMethods.NXKeytypePrevious,
        SpecialKey.BrightnessUp => (int)NativeMethods.NXKeytypeBrightnessUp,
        SpecialKey.BrightnessDown => (int)NativeMethods.NXKeytypeBrightnessDown,
        SpecialKey.Eject => (int)NativeMethods.NXKeytypeEject,
        _ => -1,
    };

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

        var kr = NativeMethods.IOServiceOpen(service, MachTaskSelf, NativeMethods.KIoHidParamConnectType, out var conn);
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
        var eventRef = NativeMethods.CGEventCreateKeyboardEvent(EventSource, vk, isDown);
        if (eventRef == nint.Zero) return;
        NativeMethods.CGEventSetType(eventRef, NativeMethods.KCGEventFlagsChanged);
        NativeMethods.CGEventSetFlags(eventRef, _modifierFlags);
        NativeMethods.CGEventPost(NativeMethods.KCGHidEventTap, eventRef);
        NativeMethods.CFRelease(eventRef);
    }

    // returns kCGEventFlagMaskSecondaryFn for vk codes that macOS hardware events carry it on.
    // fn-row keys (ForwardDelete, Home/End/PageUp/PageDown, Help, F1-F20) always have this flag set.
    // ReSharper disable once RedundantCast
    private static ulong FnFlagForVk(ushort vk) => (ulong)vk switch
    {
        MacVirtualKey.ForwardDelete or
        MacVirtualKey.Home or MacVirtualKey.End or
        MacVirtualKey.PageUp or MacVirtualKey.PageDown or
        MacVirtualKey.Help or
        MacVirtualKey.F1 or MacVirtualKey.F2 or MacVirtualKey.F3 or MacVirtualKey.F4 or
        MacVirtualKey.F5 or MacVirtualKey.F6 or MacVirtualKey.F7 or MacVirtualKey.F8 or
        MacVirtualKey.F9 or MacVirtualKey.F10 or MacVirtualKey.F11 or MacVirtualKey.F12 or
        MacVirtualKey.F13 or MacVirtualKey.F14 or MacVirtualKey.F15 or MacVirtualKey.F16 or
        MacVirtualKey.F17 or MacVirtualKey.F18 or MacVirtualKey.F19 or MacVirtualKey.F20
            => NativeMethods.KCGEventFlagMaskSecondaryFn,
        _ => 0,
    };

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
        var eventRef = NativeMethods.CGEventCreateKeyboardEvent(EventSource, 0, isDown);
        if (eventRef == nint.Zero) return;
        NativeMethods.CGEventSetFlags(eventRef, flags);
        var utf16 = (ushort)ch;
        NativeMethods.CGEventKeyboardSetUnicodeString(eventRef, 1, &utf16);
        NativeMethods.CGEventPost(NativeMethods.KCGHidEventTap, eventRef);
        NativeMethods.CFRelease(eventRef);
    }

    // build reverse map: character → vk code, from current keyboard layout.
    // first pass: unshifted (ucMods=0); second pass: shifted (ucMods=2).
    // unshifted mappings take priority. this ensures shifted chars like '%' map to their
    // base vk (0x17 for '5'), so shortcuts like Cmd+Shift+5 inject the correct virtual key.
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
            ushort* chars = stackalloc ushort[2];

            foreach (var ucMods in new uint[] { 0, 2 }) // unshifted, then shifted
            {
                for (ushort vk = 0; vk < 128; vk++)
                {
                    uint deadKeyState = 0;
                    var status = NativeMethods.UCKeyTranslate(
                        layoutPtr,
                        vk,
                        NativeMethods.KUCKeyActionDown,
                        ucMods,
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
        }
        finally
        {
            NativeMethods.CFRelease(layoutSource);
        }

        return map;
    }

    private static nint LoadTisPropertyKey()
    {
        var carbon = NativeLibrary.Load(
            "/System/Library/Frameworks/Carbon.framework/Carbon");
        return Marshal.ReadIntPtr(
            NativeLibrary.GetExport(carbon, "kTISPropertyUnicodeKeyLayoutData"));
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

    // [NSEvent doubleClickInterval] — system double-click threshold in seconds
    private static double GetDoubleClickIntervalMs()
    {
        var cls = NativeMethods.objc_getClass("NSEvent");
        var sel = NativeMethods.sel_registerName("doubleClickInterval");
        var seconds = NativeMethods.objc_msgSend_double(cls, sel);
        return seconds > 0 ? seconds * 1000.0 : 500.0;
    }

    // update _clickState for double/triple-click detection — mirrors input-leap's OSXScreen::fakeMouseButton().
    // only increments on press; release events reuse the state set by the preceding press.
    private void UpdateClickState(bool isPress)
    {
        if (!isPress) return;

        var xDiff = _mouseX - _lastSingleClickX;
        var yDiff = _mouseY - _lastSingleClickY;
        var dist = Math.Sqrt(xDiff * xDiff + yDiff * yDiff);
        var elapsedMs = Environment.TickCount64 - _lastClickTimeMs;
        if (elapsedMs <= DoubleClickIntervalMs && dist <= DoubleClickMaxDist)
            _clickState++;
        else
            _clickState = 1;

        _lastClickTimeMs = Environment.TickCount64;
        if (_clickState == 1)
        {
            _lastSingleClickX = _mouseX;
            _lastSingleClickY = _mouseY;
        }
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
        NativeMethods.EnableBackgroundCursorManipulation();
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
