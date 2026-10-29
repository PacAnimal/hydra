using System.Runtime.Versioning;
using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Relay;
using Hydra.Screen;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.MacOs;

[SupportedOSPlatform("macos")]
public sealed class MacOutputHandler : IPlatformOutput, ICursor
{
    private readonly ILogger<MacOutputHandler> _log;
    private readonly MacBrightnessController _brightness;
    private readonly MacMediaRemoteController _mediaRemote;
    // Keys whose most recent down event fell back to legacy NX injection, so the matching up event
    // mirrors it. The API-based paths (DisplayServices/DDC, CoreAudio, MediaRemote) are one-shot
    // adjustments with nothing to do on release; only the legacy NX path models a real press/release
    // pair and needs both ends sent, or the OS can see an unpaired down with no up.
    private readonly HashSet<SpecialKey> _legacyMediaKeysDown = [];

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

    private CFNotificationCallback? _layoutChangeCallback;  // keep-alive to prevent GC
    private readonly nint _layoutNotificationCenter;

    public MacOutputHandler(ILogger<MacOutputHandler> log)
    {
        _log = log;
        _brightness = new MacBrightnessController();
        _mediaRemote = new MacMediaRemoteController();
        // rebuild char→vk map whenever the user switches keyboard layout mid-session
        _layoutNotificationCenter = NativeMethods.CFNotificationCenterGetDistributedCenter();
        if (_layoutNotificationCenter != nint.Zero)
        {
            _layoutChangeCallback = (_, _, _, _, _) =>
            {
                _charToVk = BuildCharToVkMap();
                _asciiCharToVk = BuildCharToVkMap(asciiCapable: true);
            };
            using var name = new NsString(InputSourceChanged);
            NativeMethods.CFNotificationCenterAddObserver(_layoutNotificationCenter, 1, _layoutChangeCallback, name, nint.Zero,
                NativeMethods.CFNotificationSuspensionBehaviorDeliverImmediately);
        }
        GetHidConnection();
    }

    private const string InputSourceChanged = "com.apple.Carbon.TISNotifySelectedKeyboardInputSourceChanged";

    private static readonly double DoubleClickMaxDist = Math.Sqrt(2) + 0.0001;
    private static readonly double DoubleClickIntervalMs = GetDoubleClickIntervalMs();

    // char produced by each vk code (no modifiers) — rebuilt whenever keyboard layout changes
    private volatile Dictionary<char, ushort> _charToVk = BuildCharToVkMap();

    // same map from the Latin-capable layout, consulted only for shortcuts the active layout cannot
    // express: a Cmd+C arriving while a Cyrillic layout is active has no 'c' in _charToVk, and unicode
    // injection would deliver the char without the keypress an app needs to recognise the shortcut.
    private volatile Dictionary<char, ushort> _asciiCharToVk = BuildCharToVkMap(asciiCapable: true);

    public void MoveMouse(int x, int y)
    {
        var dx = _mousePositionInitialized ? x - _mouseX : 0.0;
        var dy = _mousePositionInitialized ? y - _mouseY : 0.0;
        _mouseX = x;
        _mouseY = y;
        _mousePositionInitialized = true;
        PostMove(dx, dy);
    }

    public void MoveMouseRelative(int dx, int dy)
    {
        _mouseX += dx;
        _mouseY += dy;
        _mousePositionInitialized = true;
        PostMove(dx, dy);
    }

    // posts a real move (a drag while a button is held) to the current position, so apps and OS features
    // (dock, hot corners) see it. drags need the deltas (e.g. screenshot overlay resize), and some 3D apps
    // and games read the double variant rather than the integer one.
    private void PostMove(double dx, double dy)
    {
        var move = GetMoveEventType();
        var eventRef = NativeMethods.CGEventCreateMouseEvent(nint.Zero, move.EventType, new CGPoint { X = _mouseX, Y = _mouseY }, move.Button);
        if (eventRef == nint.Zero) return;
        NativeMethods.CGEventSetIntegerValueField(eventRef, NativeMethods.KCGMouseEventClickState, _clickState);
        NativeMethods.CGEventSetFlags(eventRef, _modifierFlags);
        NativeMethods.CGEventSetIntegerValueField(eventRef, NativeMethods.KCGMouseEventDeltaX, (long)dx);
        NativeMethods.CGEventSetIntegerValueField(eventRef, NativeMethods.KCGMouseEventDeltaY, (long)dy);
        NativeMethods.CGEventSetDoubleValueField(eventRef, NativeMethods.KCGMouseEventDeltaX, dx);
        NativeMethods.CGEventSetDoubleValueField(eventRef, NativeMethods.KCGMouseEventDeltaY, dy);
        NativeHelpers.PostAndRelease(eventRef);
    }

    public void InjectKey(KeyEventMessage msg)
    {
        var isDown = msg.Type == KeyEventType.KeyDown;
        // a repeat re-presses the held key marked as autorepeat, as macOS's own repeats are
        var isRepeat = isDown && msg.IsRepeat;
        var flags = MacKeyResolver.MapModifiersToFlags(msg.Modifiers);

        if (msg.Key is { } key && key.IsModifier())
        {
            // AltGr → Option (Mac has no physical AltGr key); other modifiers look up in MacSpecialKeyMap
            ulong modVk;
            if (key == SpecialKey.AltGr)
                modVk = MacVirtualKey.RightOption;
            else if (!MacSpecialKeyMap.Instance.Reverse.TryGetValue(key, out modVk))
            {
                // not in Reverse (e.g. ScrollLock) — check OutputOverrides (ScrollLock → F14)
                if (MacSpecialKeyMap.OutputOverrides.TryGetValue(key, out var ovr))
                    PostCgKey(ovr.Vk, isDown, flags | ovr.ExtraFlags, isRepeat: false);
                return;
            }

            var (generic, device, _) = MacModifierMasks.Of(modVk);
            if (generic == 0 && device == 0)
            {
                // NumLock (KeypadClear) has no modifier mask — inject as a regular toggle key, not a flags change
                PostKey((ushort)modVk, isDown, flags, isRepeat: false);
                return;
            }

            // update accumulated modifier state, then inject via IOHIDPostEvent (NX_FLAGSCHANGED) to update
            // the system-wide HID modifier state read by [NSEvent modifierFlags] class method (what Chromium queries).
            // falls back to CGEventPost when IOKit unavailable.
            if (isDown) _deviceModifierFlags |= device;
            else _deviceModifierFlags &= ~device;
            // CapsLock has no device bit — toggle generic flag on KeyDown only (it's a toggle key, not a hold key)
            if (generic == NativeMethods.KCGEventFlagMaskAlphaShift && isDown)
                _modifierFlags ^= NativeMethods.KCGEventFlagMaskAlphaShift;
            // derive generic flags from device bits so L+R held simultaneously keeps the generic flag set
            _modifierFlags = MacModifierMasks.GenericFlags(_deviceModifierFlags) | (_modifierFlags & NativeMethods.KCGEventFlagMaskAlphaShift);

            if (!PostHidModifier((ushort)modVk))
                PostFlagsChanged((ushort)modVk, isDown);
        }
        else if (msg.Character is { } ch)
        {
            if (isDown) SyncCapsLock(msg.Modifiers);

            // AltGr characters: inject via unicode string — the VK path requires knowing which
            // Mac key+modifier combination produces the char, but AltGr chars from Linux/Windows
            // layouts have no Mac equivalent (e.g. AltGr+4='$' on Norwegian: CharToVk finds '$'→VK_4
            // but without Shift in modifier state, VK_4 produces '4' instead).
            // for regular and Super/Ctrl shortcut keys, use IOHIDPostEvent so the shortcut handler
            // fires correctly before the character reaches the focused app.
            var isAltGr = (msg.Modifiers & KeyModifiers.AltGr) != 0;

            if (!isAltGr && _charToVk.TryGetValue(ch, out var charVk))
            {
                PostKey(charVk, isDown, flags, isRepeat);
            }
            else if (!isAltGr && _charToVk.TryGetValue(char.ToLowerInvariant(ch), out charVk))
            {
                // lowercase-VK fallback for an uppercase char. PostHidKey relies on global HID modifier
                // state for Shift; if Shift is not yet in _deviceModifierFlags (e.g. focus arrived while
                // Shift was already held), use PostCgKey which applies flags — including Shift — directly.
                var shiftReady = (_deviceModifierFlags & (NativeMethods.NxDeviceLShiftKeyMask | NativeMethods.NxDeviceRShiftKeyMask)) != 0;
                PostKey(charVk, isDown, flags, isRepeat, viaHid: shiftReady);
            }
            else if (!isAltGr && (flags & (NativeMethods.KCGEventFlagMaskCommand | NativeMethods.KCGEventFlagMaskControl)) != 0 &&
                     _asciiCharToVk.TryGetValue(ch, out charVk))
            {
                PostKey(charVk, isDown, flags, isRepeat);
            }
            else
            {
                // AltGr chars are injected as unicode — strip Option and Shift so the CGEvent doesn't
                // re-trigger Option-based composition or text-selection behaviour in the receiving app.
                // Shift was consumed by level-3 resolution on the master; it must not reach the slave.
                InjectCharacter(ch, isDown, isAltGr
                    ? flags & ~(NativeMethods.KCGEventFlagMaskAlternate | NativeMethods.KCGEventFlagMaskShift)
                    : flags, isRepeat);
            }
        }
        else if (msg.Key is { } key2)
        {
            if (isDown && key2 != SpecialKey.CapsLock) SyncCapsLock(msg.Modifiers);

            if (key2 == SpecialKey.MissionControl)
            {
                if (isDown) System.Diagnostics.Process.Start("open", ["-a", "Mission Control"]);
            }
            else if (key2 is SpecialKey.BrightnessUp or SpecialKey.BrightnessDown)
                HandleMediaKeyWithLegacyFallback(key2, isDown,
                    () => _brightness.TryAdjustMainDisplay(key2 == SpecialKey.BrightnessUp));
            else if (key2 is SpecialKey.AudioVolumeUp or SpecialKey.AudioVolumeDown)
                HandleMediaKeyWithLegacyFallback(key2, isDown,
                    () => MacAudioController.TryAdjustVolume(key2 == SpecialKey.AudioVolumeUp));
            else if (key2 == SpecialKey.AudioMute)
                HandleMediaKeyWithLegacyFallback(key2, isDown, MacAudioController.TryToggleMute);
            else if (key2 is SpecialKey.AudioPlay or SpecialKey.AudioNext or SpecialKey.AudioPrev)
                HandleMediaKeyWithLegacyFallback(key2, isDown, () => _mediaRemote.TrySend(key2));
            // media keys require NX_SYSDEFINED injection via NSEvent — regular NX_KEYDOWN with the VK
            // produces wrong results (volume VKs hit wrong keys in the regular keycode space).
            else if (MacMediaKeyMap.TryGetNxKeyType(key2, out var nxType))
                PostNsMediaKey(nxType, isDown);
            else if (MacSpecialKeyMap.OutputOverrides.TryGetValue(key2, out var ovr))
                PostCgKey(ovr.Vk, isDown, flags | ovr.ExtraFlags, isRepeat);
            // macOS has no numlock: a keypad navigation key is its dedicated twin
            else if (MacSpecialKeyMap.Instance.Reverse.TryGetValue(key2.WithoutKeypad(), out var vk))
            {
                // non-modifier special key: IOHIDPostEvent-first, CGEvent fallback
                PostKey((ushort)vk, isDown, flags, isRepeat);
            }
        }
    }

    // syncs slave CapsLock state to match the master's modifier bits before injecting a key event.
    // IOHIDPostEvent relies on the global HID modifier state (NX_FLAGSCHANGED); if master had CapsLock
    // on at connect time, _modifierFlags starts at 0 on the slave and must be corrected here.
    private void SyncCapsLock(KeyModifiers mods)
    {
        var want = (mods & KeyModifiers.CapsLock) != 0;
        var have = (_modifierFlags & NativeMethods.KCGEventFlagMaskAlphaShift) != 0;
        if (want == have) return;
        _modifierFlags ^= NativeMethods.KCGEventFlagMaskAlphaShift;
        if (!PostHidModifier((ushort)MacVirtualKey.CapsLock))
            PostFlagsChanged((ushort)MacVirtualKey.CapsLock, true);
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
        NativeHelpers.PostAndRelease(eventRef);
    }

    public void InjectMouseScroll(MouseScrollMessage msg)
    {
        if (msg.YDelta == 0 && msg.XDelta == 0) return;

        // one notch is one line: whole lines for the event constructor (kCGScrollEventUnitLine = 1)
        var eventRef = NativeMethods.CGEventCreateScrollWheelEvent(nint.Zero, 1, 2, msg.YDelta / MouseScrollEvent.Notch, msg.XDelta / MouseScrollEvent.Notch);
        if (eventRef == nint.Zero) return;

        // set 16.16 fixed-point line deltas for sub-line precision (reverses the input handler's conversion)
        var yFixed = (long)msg.YDelta * 65536 / MouseScrollEvent.Notch;
        var xFixed = (long)msg.XDelta * 65536 / MouseScrollEvent.Notch;
        NativeMethods.CGEventSetIntegerValueField(eventRef, NativeMethods.KCGScrollWheelEventFixedPtDeltaAxis1, yFixed);
        NativeMethods.CGEventSetIntegerValueField(eventRef, NativeMethods.KCGScrollWheelEventFixedPtDeltaAxis2, xFixed);
        NativeHelpers.PostAndRelease(eventRef);
    }

    // through IOHIDPostEvent where it can, else as a CG event
    private void PostKey(ushort vk, bool isDown, ulong flags, bool isRepeat, bool viaHid = true)
    {
        if (!viaHid || !PostHidKey(vk, isDown, isRepeat)) PostCgKey(vk, isDown, flags, isRepeat);
    }

    // post a CG keyboard event, with its flags applied directly
    private static void PostCgKey(ushort vk, bool isDown, ulong flags, bool isRepeat)
    {
        var eventRef = NativeMethods.CGEventCreateKeyboardEvent(NativeHelpers.EventSource, vk, isDown);
        if (eventRef == nint.Zero) return;
        NativeMethods.CGEventSetFlags(eventRef, flags | FnFlagForVk(vk));
        MarkRepeat(eventRef, isRepeat);
        NativeHelpers.PostAndRelease(eventRef);
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

    // post NX_KEYDOWN/NX_KEYUP via IOHIDPostEvent.
    // eventFlags carries only the Fn flag (where applicable); modifier context comes from the global
    // HID state set by NX_FLAGSCHANGED — adding modifiers inline causes character translation
    // (e.g. VK_4 + Shift → '¤' on Norwegian) to happen before the shortcut handler fires.
    private bool PostHidKey(ushort vk, bool isDown, bool isRepeat)
    {
        var conn = GetHidConnection();
        if (conn == 0) return false;

        var eventData = new NXEventData { KeyCode = vk, Repeat = (short)(isRepeat ? 1 : 0) };
        var eventType = isDown ? NativeMethods.NxKeyDown : NativeMethods.NxKeyUp;
        var eventFlags = (uint)FnFlagForVk(vk);
        var kr = NativeMethods.IOHIDPostEvent(conn, eventType, default, in eventData,
            NativeMethods.KNxEventDataVersion, eventFlags, 0);
        if (kr != 0)
            _log.LogWarning("IOHIDPostEvent(NX_KEY{Dir}) failed: kr={Kr} vk=0x{Vk:x2}", isDown ? "DOWN" : "UP", kr, vk);
        return kr == 0;
    }

    private static readonly nint SelOtherEvent =
        NativeMethods.sel_registerName("otherEventWithType:location:modifierFlags:timestamp:windowNumber:context:subtype:data1:data2:");
    private static readonly nint SelCgEvent = NativeMethods.sel_registerName("CGEvent");

    // tries the real API on a down press; on release, replays legacy NX injection only if the down
    // press actually used it (never re-invokes tryApi, which would repeat a stateful adjustment).
    private void HandleMediaKeyWithLegacyFallback(SpecialKey key, bool isDown, Func<bool> tryApi)
    {
        if (isDown)
        {
            if (!tryApi() && MacMediaKeyMap.TryGetNxKeyType(key, out var nxType))
            {
                _legacyMediaKeysDown.Add(key);
                PostNsMediaKey(nxType, true);
            }
        }
        else if (_legacyMediaKeysDown.Remove(key) && MacMediaKeyMap.TryGetNxKeyType(key, out var nxType))
        {
            PostNsMediaKey(nxType, false);
        }
    }

    // inject a media key via [NSEvent otherEventWithType:NSSystemDefined subtype:8 ...] → CGEventPost.
    // mirrors barrier/deskflow's fakeNativeMediaKey(): NX_SYSDEFINED is the only reliable path for
    // volume, brightness, eject, play/next/prev — regular NX_KEYDOWN with the VK code misroutes.
    private static void PostNsMediaKey(uint keyType, bool isDown)
    {
        // the autoreleased NSEvent lives until its CGEvent is posted
        using var pool = new ObjcAutoreleasePool();

        // data1: high 16 bits = NX_KEYTYPE, bits 8–15 = 0x0a (down) or 0x0b (up)
        var data1 = (nint)((keyType << 16) | (isDown ? 0x0a00u : 0x0b00u));

        var nsEvent = NativeMethods.objc_msgSend_NSEvent_otherEvent(
            NativeHelpers.NsEventClass, SelOtherEvent,
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
        var cgEventRef = NativeMethods.objc_msgSend_noarg(nsEvent, SelCgEvent);
        if (cgEventRef == nint.Zero) return;
        NativeMethods.CGEventPost(NativeMethods.KCGHidEventTap, cgEventRef);
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

        var kr = NativeMethods.IOServiceOpen(service, NativeMethods.MachTaskSelf, NativeMethods.KIoHidParamConnectType, out var conn);
        _ = NativeMethods.IOObjectRelease(service);
        if (kr != 0)
        {
            _log.LogWarning("IOServiceOpen failed: kr={Kr} — IOHIDPostEvent unavailable", kr);
            return 0;
        }

        if (_log.IsEnabled(LogLevel.Information)) _log.LogInformation("IOKit HID connection established (conn={Conn})", conn);
        _hidConnection = conn;
        return _hidConnection;
    }

    // fallback: post kCGEventFlagsChanged via CGEventPost when IOHIDPostEvent unavailable.
    // mirrors deskflow's postKeyboardKey() fallback path.
    private void PostFlagsChanged(ushort vk, bool isDown)
    {
        var eventRef = NativeMethods.CGEventCreateKeyboardEvent(NativeHelpers.EventSource, vk, isDown);
        if (eventRef == nint.Zero) return;
        NativeMethods.CGEventSetType(eventRef, NativeMethods.KCGEventFlagsChanged);
        NativeMethods.CGEventSetFlags(eventRef, _modifierFlags);
        NativeHelpers.PostAndRelease(eventRef);
    }

    // returns the extra event flags that macOS hardware events carry for certain vk codes.
    // fn-row keys (ForwardDelete, Home/End/PageUp/PageDown, Help, F1-F20) carry kCGEventFlagMaskSecondaryFn.
    // numpad keys carry kCGEventFlagMaskNumericPad so that apps checking NSEventModifierFlagNumericPad work.
    private static ulong FnFlagForVk(ulong vk) => vk switch
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
        MacVirtualKey.KeypadClear or
        MacVirtualKey.KeypadEnter or
        MacVirtualKey.KeypadDecimal or
        MacVirtualKey.KeypadMultiply or
        MacVirtualKey.KeypadPlus or
        MacVirtualKey.KeypadDivide or
        MacVirtualKey.KeypadMinus or
        MacVirtualKey.KeypadEquals or
        MacVirtualKey.Keypad0 or MacVirtualKey.Keypad1 or MacVirtualKey.Keypad2 or
        MacVirtualKey.Keypad3 or MacVirtualKey.Keypad4 or MacVirtualKey.Keypad5 or
        MacVirtualKey.Keypad6 or MacVirtualKey.Keypad7 or MacVirtualKey.Keypad8 or
        MacVirtualKey.Keypad9
            => NativeMethods.KCGEventFlagMaskNumericPad,
        _ => 0,
    };

    private static void MarkRepeat(nint eventRef, bool isRepeat)
    {
        if (isRepeat) NativeMethods.CGEventSetIntegerValueField(eventRef, NativeMethods.KCGKeyboardEventAutorepeat, 1);
    }

    // fallback character injection for chars with no VK mapping (foreign/composed chars).
    // vk=0 with explicit unicode string — only reached when _charToVk has no entry for the char.
    private static unsafe void InjectCharacter(char ch, bool isDown, ulong flags, bool isRepeat)
    {
        var eventRef = NativeMethods.CGEventCreateKeyboardEvent(NativeHelpers.EventSource, 0, isDown);
        if (eventRef == nint.Zero) return;
        NativeMethods.CGEventSetFlags(eventRef, flags);
        MarkRepeat(eventRef, isRepeat);
        var utf16 = (ushort)ch;
        NativeMethods.CGEventKeyboardSetUnicodeString(eventRef, 1, &utf16);
        NativeHelpers.PostAndRelease(eventRef);
    }

    // build reverse map: character → vk code, from current keyboard layout.
    // first pass: unshifted (ucMods=0); second pass: shifted (ucMods=2).
    // unshifted mappings take priority. this ensures shifted chars like '%' map to their
    // base vk (0x17 for '5'), so shortcuts like Cmd+Shift+5 inject the correct virtual key.
    private static unsafe Dictionary<char, ushort> BuildCharToVkMap(bool asciiCapable = false)
    {
        var map = new Dictionary<char, ushort>();

        using var layout = KeyboardLayout.Open(asciiCapable);
        var layoutPtr = layout.Data;
        if (layoutPtr == nint.Zero) return map;

        var kbdType = NativeMethods.LMGetKbdType();
        ushort* chars = stackalloc ushort[2];

        foreach (var ucMods in new uint[] { 0, 2 }) // unshifted, then shifted
        {
            for (ushort vk = 0; vk < 128; vk++)
            {
                // probe without kUCKeyTranslateNoDeadKeysBit: if deadKeyState goes non-zero the
                // key is a dead key at this level. injecting that VK on the slave would start a
                // dead-key sequence instead of producing the char — exclude it from the map.
                uint deadProbeState = 0;
                _ = NativeMethods.UCKeyTranslate(layoutPtr, vk, NativeMethods.KUCKeyActionDown, ucMods, kbdType, 0, ref deadProbeState, 2, out _, chars);
                if (deadProbeState != 0) continue;

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

        return map;
    }

    // [NSEvent doubleClickInterval] — system double-click threshold in seconds
    private static double GetDoubleClickIntervalMs()
    {
        var sel = NativeMethods.sel_registerName("doubleClickInterval");
        var seconds = NativeMethods.objc_msgSend_double(NativeHelpers.NsEventClass, sel);
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

    public bool IsAccessibilityTrusted() => NativeHelpers.EnsureAccessibilityTrusted();
    public Task WaitForAccessibilityTrusted(CancellationToken cancel) => NativeHelpers.WaitForAccessibilityTrusted(cancel);

    public ScreenPoint? GetCursorPosition() => MacDisplayHelper.CursorPosition();

    public ValueTask HideCursor()
    {
        if (_cursorHidden) return ValueTask.CompletedTask;
        NativeHelpers.EnableBackgroundCursorManipulation();
        var err = NativeMethods.CGDisplayHideCursor(_display);
        if (err != 0) _log.LogWarning("CGDisplayHideCursor failed (error {Error})", err);
        _cursorHidden = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask ShowCursor()
    {
        if (!_cursorHidden) return ValueTask.CompletedTask;
        NativeHelpers.EnableBackgroundCursorManipulation();
        _ = NativeMethods.CGDisplayShowCursor(_display);
        _cursorHidden = false;
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        if (_layoutNotificationCenter != nint.Zero)
        {
            using (var name = new NsString(InputSourceChanged))
                NativeMethods.CFNotificationCenterRemoveObserver(_layoutNotificationCenter, 1, name, nint.Zero);
            _layoutChangeCallback = null;
        }
        if (_cursorHidden)
            _ = NativeMethods.CGDisplayShowCursor(_display);
        if (_hidConnection != 0)
        {
            var result = NativeMethods.IOServiceClose(_hidConnection);
            if (result != 0) _log.LogWarning("IOServiceClose failed: kr={Kr}", result);
            _hidConnection = 0;
        }
    }

    private record MoveEvent(int EventType, int Button);
}
