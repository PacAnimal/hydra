using System.Runtime.InteropServices;
using Cathedral.Utils;
using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Screen;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.MacOs;

internal sealed class MacInputHandler(ILogger<MacInputHandler> log, MacShieldProcess shield) : IPlatformInput
{
    private readonly MacShieldProcess _shield = shield;
    private readonly uint _display = NativeMethods.CGMainDisplayID();
    private readonly nint _cfBooleanTrue = GetCFBooleanTrue();
    private readonly MacKeyResolver _keyResolver = new();

    // stored as fields to prevent GC collection while the tap is active
    private CGEventTapCallBack? _tapCallback;
    private Thread? _tapThread;
    private nint _runLoop;
    private nint _tapPort;
    private nint _runLoopSource;
    private Action<double, double>? _onMouseMove;
    private Action<KeyEvent>? _onKeyEvent;
    private Action<MouseButtonEvent>? _onMouseButton;
    private Action<MouseScrollEvent>? _onMouseScroll;
    private bool _cursorHidden;
    private readonly Toggle _isOnVirtualScreen = new();
    public bool IsOnVirtualScreen { get => _isOnVirtualScreen; set => _isOnVirtualScreen.TrySet(value); }

    // cached ObjC selectors for NX_SYSDEFINED media key decoding and repeat settings
    private static readonly nint _nsEventClass = NativeMethods.objc_getClass("NSEvent");
    private static readonly nint _selKeyRepeatDelay = NativeMethods.sel_registerName("keyRepeatDelay");
    private static readonly nint _selKeyRepeatInterval = NativeMethods.sel_registerName("keyRepeatInterval");
    private static readonly nint _selEventWithCGEvent = NativeMethods.sel_registerName("eventWithCGEvent:");
    private static readonly nint _selSubtype = NativeMethods.sel_registerName("subtype");
    private static readonly nint _selData1 = NativeMethods.sel_registerName("data1");


    public bool IsAccessibilityTrusted() => NativeMethods.AXIsProcessTrusted();

    public void WarpCursor(int x, int y)
    {
        // SYSLIB1054: return value intentionally discarded (CG error codes are advisory only)
        _ = NativeMethods.CGWarpMouseCursorPosition(new CGPoint { X = x, Y = y });
    }

    public void HideCursor()
    {
        if (_cursorHidden) return;
        _shield.Show();

        // allow cursor manipulation from background (private CGS API -- matches synergy)
        var cid = NativeMethods.CGSMainConnectionID();
        var key = NativeMethods.CFStringCreateWithCString(nint.Zero, "SetsCursorInBackground", NativeMethods.KCFStringEncodingUtf8);
        _ = NativeMethods.CGSSetConnectionProperty(cid, cid, key, _cfBooleanTrue);
        NativeMethods.CFRelease(key);

        _ = NativeMethods.CGDisplayHideCursor(_display);
        _ = NativeMethods.CGAssociateMouseAndMouseCursorPosition(true);
        // near-zero suppression interval prevents CGWarpMouseCursorPosition from resetting acceleration
        NativeMethods.CGSetLocalEventsSuppressionInterval(0.0001);
        _cursorHidden = true;
    }

    public void ShowCursor()
    {
        if (!_cursorHidden) return;
        _shield.Hide();
        _ = NativeMethods.CGDisplayShowCursor(_display);
        _ = NativeMethods.CGAssociateMouseAndMouseCursorPosition(true);
        NativeMethods.CGSetLocalEventsSuppressionInterval(0.0);
        _cursorHidden = false;
    }

    public void StartEventTap(
        Action<double, double> onMouseMove,
        Action<KeyEvent> onKeyEvent,
        Action<MouseButtonEvent> onMouseButton,
        Action<MouseScrollEvent> onMouseScroll)
    {
        _onMouseMove = onMouseMove;
        _onKeyEvent = onKeyEvent;
        _onMouseButton = onMouseButton;
        _onMouseScroll = onMouseScroll;

        // callback must be stored as field -- will crash if collected
        _tapCallback = TapCallback;

        using var ready = new ManualResetEventSlim(false);

        _tapThread = new Thread(() =>
        {
            _runLoop = NativeMethods.CFRunLoopGetCurrent();

            _tapPort = NativeMethods.CGEventTapCreate(
                NativeMethods.KCGHidEventTap,
                NativeMethods.KCGHeadInsertEventTap,
                NativeMethods.KCGEventTapOptionDefault,
                NativeMethods.KCGEventMaskForAllEvents,
                _tapCallback,
                nint.Zero);

            if (_tapPort == nint.Zero)
            {
                log.LogError("CGEventTapCreate returned null -- accessibility permission denied?");
                ready.Set();
                return;
            }

            var commonModes = GetCFRunLoopCommonModes();
            _runLoopSource = NativeMethods.CFMachPortCreateRunLoopSource(nint.Zero, _tapPort, 0);
            NativeMethods.CFRunLoopAddSource(_runLoop, _runLoopSource, commonModes);
            NativeMethods.CGEventTapEnable(_tapPort, true);

            ready.Set();
            NativeMethods.CFRunLoopRun();

            // cleanup after run loop stops
            if (_runLoopSource != nint.Zero) NativeMethods.CFRelease(_runLoopSource);
            if (_tapPort != nint.Zero) NativeMethods.CFRelease(_tapPort);
        })
        { IsBackground = true, Name = "HydraEventTap" };

        _tapThread.Start();
        ready.Wait();
    }

    public KeyRepeatSettings GetKeyRepeatSettings()
    {
        if (_nsEventClass == nint.Zero) return new KeyRepeatSettings(500, 33);
        var delaySeconds = NativeMethods.objc_msgSend_double(_nsEventClass, _selKeyRepeatDelay);
        var rateSeconds = NativeMethods.objc_msgSend_double(_nsEventClass, _selKeyRepeatInterval);
        return new KeyRepeatSettings((int)(delaySeconds * 1000), (int)(rateSeconds * 1000));
    }

    public void StopEventTap()
    {
        if (_runLoop != nint.Zero)
            NativeMethods.CFRunLoopStop(_runLoop);
        _tapThread?.Join(TimeSpan.FromSeconds(2));
    }

    public void Dispose()
    {
        StopEventTap();
        if (_cursorHidden) ShowCursor();
        GC.SuppressFinalize(this);
    }

    private nint TapCallback(nint proxy, int type, nint eventRef, nint userInfo)
    {
        if (type == NativeMethods.KCGEventTapDisabledByTimeout)
        {
            log.LogWarning("Event tap disabled by timeout, re-enabling");
            NativeMethods.CGEventTapEnable(_tapPort, true);
            return eventRef;
        }

        if (type is NativeMethods.KCGEventMouseMoved
            or NativeMethods.KCGEventLeftMouseDragged
            or NativeMethods.KCGEventRightMouseDragged
            or NativeMethods.KCGEventOtherMouseDragged)
        {
            var pos = NativeMethods.CGEventGetLocation(eventRef);
            _onMouseMove?.Invoke(pos.X, pos.Y);
            return eventRef;
        }

        if (type is NativeMethods.KCGEventLeftMouseDown or NativeMethods.KCGEventLeftMouseUp
            or NativeMethods.KCGEventRightMouseDown or NativeMethods.KCGEventRightMouseUp
            or NativeMethods.KCGEventOtherMouseDown or NativeMethods.KCGEventOtherMouseUp)
        {
            var isDown = type is NativeMethods.KCGEventLeftMouseDown or NativeMethods.KCGEventRightMouseDown or NativeMethods.KCGEventOtherMouseDown;
            var cgButton = (int)NativeMethods.CGEventGetIntegerValueField(eventRef, NativeMethods.KCGMouseEventButtonNumber);
            var button = CgButtonToMouseButton(cgButton);
            _onMouseButton?.Invoke(new MouseButtonEvent(button, isDown));
            return IsOnVirtualScreen ? nint.Zero : eventRef;
        }

        if (type == NativeMethods.KCGEventScrollWheel)
        {
            // read 16.16 fixed-point line deltas (precision scrolling from trackpads and hi-res mice).
            // convert to 120-unit wire format: fixedPt * 120 >> 16 (i.e. 1.0 lines = 120 wire units).
            var fpDy = NativeMethods.CGEventGetIntegerValueField(eventRef, NativeMethods.KCGScrollWheelEventFixedPtDeltaAxis1);
            var fpDx = NativeMethods.CGEventGetIntegerValueField(eventRef, NativeMethods.KCGScrollWheelEventFixedPtDeltaAxis2);
            if (fpDx != 0 || fpDy != 0)
                _onMouseScroll?.Invoke(new MouseScrollEvent(
                    (short)Math.Clamp(fpDx * 120L >> 16, short.MinValue, short.MaxValue),
                    (short)Math.Clamp(fpDy * 120L >> 16, short.MinValue, short.MaxValue)));
            return IsOnVirtualScreen ? nint.Zero : eventRef;
        }

        if (type is NativeMethods.KCGEventKeyDown
            or NativeMethods.KCGEventKeyUp
            or NativeMethods.KCGEventFlagsChanged)
        {
            // always resolve to track modifier state even on the real screen
            var keyEvent = _keyResolver.Resolve(type, eventRef);
            if (keyEvent is not null)
                _onKeyEvent?.Invoke(keyEvent);
            return IsOnVirtualScreen ? nint.Zero : eventRef;
        }

        // NX_SYSDEFINED (type 14): media keys — play/pause/next/prev/brightness/eject
        if (type == NativeMethods.KNXSysDefined)
        {
            HandleMediaKeyEvent(eventRef);
            return IsOnVirtualScreen ? nint.Zero : eventRef;
        }

        // swallow all other events while on virtual screen (synergy: return nullptr when off-screen)
        return IsOnVirtualScreen ? nint.Zero : eventRef;
    }

    // CG button numbers: 0=left, 1=right, 2=middle, 3+=extra
    private static MouseButton CgButtonToMouseButton(int cgButton) => cgButton switch
    {
        0 => MouseButton.Left,
        1 => MouseButton.Right,
        2 => MouseButton.Middle,
        3 => MouseButton.Extra1,
        _ => MouseButton.Extra2,
    };

    private void HandleMediaKeyEvent(nint eventRef)
    {
        // create NSEvent from CGEvent to read subtype and data1
        if (_nsEventClass == nint.Zero) return;
        var nsEvent = NativeMethods.objc_msgSend(_nsEventClass, _selEventWithCGEvent, eventRef);
        if (nsEvent == nint.Zero) return;

        // subtype 8 = NSSystemDefinedEvent (NSEventSubtypeApplicationActivated is different)
        var subtype = NativeMethods.objc_msgSend_long(nsEvent, _selSubtype);
        if (subtype != 8) return;

        var data1 = NativeMethods.objc_msgSend_long(nsEvent, _selData1);
        var nxKeyType = (uint)((data1 & 0xFFFF0000L) >> 16);
        var isDown = (data1 & 0x100) == 0;

        var specialKey = NxKeyTypeToSpecialKey(nxKeyType);
        if (!specialKey.HasValue) return;

        var keyEvent = KeyEvent.Special(isDown ? KeyEventType.KeyDown : KeyEventType.KeyUp, specialKey.Value, KeyModifiers.None);
        _onKeyEvent?.Invoke(keyEvent);
    }

    private static SpecialKey? NxKeyTypeToSpecialKey(uint type) => type switch
    {
        NativeMethods.NXKeytypeSoundUp => SpecialKey.AudioVolumeUp,
        NativeMethods.NXKeytypeSoundDown => SpecialKey.AudioVolumeDown,
        NativeMethods.NXKeytypeMute => SpecialKey.AudioMute,
        NativeMethods.NXKeytypeEject => SpecialKey.Eject,
        NativeMethods.NXKeytypePlay => SpecialKey.AudioPlay,
        NativeMethods.NXKeytypeNext or NativeMethods.NXKeytypeFast => SpecialKey.AudioNext,
        NativeMethods.NXKeytypePrevious or NativeMethods.NXKeytypeRewind => SpecialKey.AudioPrev,
        NativeMethods.NXKeytypeBrightnessUp => SpecialKey.BrightnessUp,
        NativeMethods.NXKeytypeBrightnessDown => SpecialKey.BrightnessDown,
        _ => null,
    };

    private static nint GetCFRunLoopCommonModes() => ReadCoreFoundationSymbol("kCFRunLoopCommonModes");
    private static nint GetCFBooleanTrue() => ReadCoreFoundationSymbol("kCFBooleanTrue");

    private static readonly nint _coreFoundation =
        NativeLibrary.Load("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation");

    private static nint ReadCoreFoundationSymbol(string name) =>
        Marshal.ReadIntPtr(NativeLibrary.GetExport(_coreFoundation, name));
}
