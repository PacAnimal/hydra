using System.Runtime.InteropServices;
using Cathedral.Utils;
using Hydra.Config;
using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Screen;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Linux;

public sealed class XorgInputHandler : IPlatformInput
{
    private readonly nint _display;
    private readonly nint _rootWindow;
    private readonly nint _inputSink;
    private readonly int _xiOpcode;
    private readonly List<GrabbedKey> _hotkeys;
    private readonly bool _detectableAutoRepeat;
    private readonly XorgKeyResolver _keyResolver = new();

    private readonly ILogger<XorgInputHandler> _log;
    private Thread? _eventThread;
    private volatile bool _running;
    private Action<double, double>? _onMouseMove;
    private Action<double, double>? _onMouseDelta;
    private Action<KeyEvent>? _onKeyEvent;
    private Action<MouseButtonEvent>? _onMouseButton;
    private Action<MouseScrollEvent>? _onMouseScroll;
    private Action? _onLocalActivity;
    private bool _cursorHidden;
    private readonly Toggle _isOnVirtualScreen = new();
    private readonly Lock _grabLock = new();
    private readonly XGrabSession _keyboard;
    private readonly XGrabSession _pointer;
    private readonly bool _injectedInputReturnsHome;
    // each source device seen on a raw event, and whether it is an XTEST device (another client's injected input);
    // only the event thread touches it, and a hierarchy change empties it, since a device id can be reused
    private readonly Dictionary<int, bool> _xtestSources = [];

    // XI2 event mask for raw key press (13), raw button press/release (15, 16) and raw motion (17).
    // XISetMask(mask, n) = mask[n>>3] |= 1 << (n&7)
    private static readonly byte[] Xi2Mask =
    [
        0,
        (1 << (NativeMethods.XI_RawKeyPress & 7)) | (1 << (NativeMethods.XI_RawButtonPress & 7)),  // byte 1: bits 5+7 (events 13, 15)
        (1 << (NativeMethods.XI_RawButtonRelease & 7)) | (1 << (NativeMethods.XI_RawMotion & 7)),  // byte 2: bits 0+1 (events 16, 17)
        0,
    ];

    // XI2 event mask for hierarchy changes (11), selected for all devices: a device added or removed
    private static readonly byte[] HierarchyMask =
    [
        0,
        1 << (NativeMethods.XI_HierarchyChanged & 7),  // byte 1: bit 3 (event 11)
    ];

    public XorgInputHandler(ILogger<XorgInputHandler> log, IHydraProfile profile)
    {
        _log = log;
        _injectedInputReturnsHome = profile.InjectedInputReturnsHome;
        _display = XlibRuntime.OpenDisplayOrThrow("input capture");

        _rootWindow = NativeMethods.XDefaultRootWindow(_display);
        _inputSink = CreateInputSink();
        _xiOpcode = DetectXi2();
        _hotkeys = XorgHotkeyGrab.Keys(_display, profile.Hotkeys);
        _keyResolver.Init(_display);
        _detectableAutoRepeat = XorgDisplayHelper.EnableDetectableAutoRepeat(_display);
        if (!_detectableAutoRepeat)
            log.LogWarning("X server lacks detectable auto-repeat; telling a repeat from a quick re-press relies on peeking the event queue");

        _keyboard = new XGrabSession("Keyboard", _grabLock, log,
            preGrab: () =>
            {
                ResetHeldKeys();
                RaiseInputSink();
                _ = NativeMethods.XFlush(_display);
            },
            grab: () =>
            {
                var r = NativeMethods.XGrabKeyboard(_display, _inputSink, true,
                    NativeMethods.GrabModeAsync, NativeMethods.GrabModeAsync, NativeMethods.CurrentTime);
                _ = NativeMethods.XFlush(_display);
                if (r == NativeMethods.GrabSuccess) _keyResolver.ForgetReleasedSeeds(XorgDisplayHelper.HeldKeycodes(_display));
                return r;
            },
            preRetry: () =>
            {
                // keys may have gone down or up while the grab was unowned
                ResetHeldKeys();
                // dismiss transient grabs (xsecurelock technique: redirect focus to pointer root)
                _ = NativeMethods.XSetInputFocus(_display, NativeMethods.PointerRoot,
                    NativeMethods.RevertToPointerRoot, NativeMethods.CurrentTime);
                _ = NativeMethods.XFlush(_display);
            },
            ungrab: () =>
            {
                _ = NativeMethods.XUngrabKeyboard(_display, NativeMethods.CurrentTime);
                _ = NativeMethods.XFlush(_display);
            },
            gaveUp: () => CaptureFailed?.Invoke(),
            wonLate: () => KeyboardCapturedLate?.Invoke());

        _pointer = new XGrabSession("Pointer", _grabLock, log,
            preGrab: () =>
            {
                RaiseInputSink();
                _ = NativeMethods.XFlush(_display);
            },
            grab: () =>
            {
                var r = NativeMethods.XGrabPointer(_display, _inputSink, false,
                    NativeMethods.ButtonPressMask | NativeMethods.ButtonReleaseMask | NativeMethods.PointerMotionMask,
                    NativeMethods.GrabModeAsync, NativeMethods.GrabModeAsync,
                    nint.Zero, nint.Zero, NativeMethods.CurrentTime);
                _ = NativeMethods.XFlush(_display);
                return r;
            },
            preRetry: null,
            ungrab: () =>
            {
                _ = NativeMethods.XUngrabPointer(_display, NativeMethods.CurrentTime);
                _ = NativeMethods.XFlush(_display);
            },
            gaveUp: () => CaptureFailed?.Invoke());
    }

    public void WarpCursor(int x, int y)
    {
        // SYSLIB1054: return value intentionally discarded (X error codes are advisory only)
        _ = NativeMethods.XWarpPointer(_display, nint.Zero, _rootWindow, 0, 0, 0, 0, x, y);
        _ = NativeMethods.XFlush(_display);
    }

    public ScreenPoint? GetCursorPosition() => XorgDisplayHelper.CursorPosition(_display, _rootWindow);

    public ValueTask HideCursor()
    {
        if (_cursorHidden) return ValueTask.CompletedTask;
        RaiseInputSink();
        NativeMethods.XFixesHideCursor(_display, _rootWindow);
        _ = NativeMethods.XFlush(_display);
        _cursorHidden = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask ShowCursor()
    {
        if (!_cursorHidden) return ValueTask.CompletedTask;
        _ = NativeMethods.XUnmapWindow(_display, _inputSink);
        NativeMethods.XFixesShowCursor(_display, _rootWindow);
        _ = NativeMethods.XFlush(_display);
        _cursorHidden = false;
        return ValueTask.CompletedTask;
    }

    public event Action? CaptureFailed;
    public event Action? KeyboardCapturedLate;
    public event Action<ScreenPoint?>? InjectedInput;

    public bool IsOnVirtualScreen
    {
        get => _isOnVirtualScreen;
        set
        {
            _isOnVirtualScreen.TrySet(value);
            if (value)
            {
                _pointer.Grab();
                _keyboard.Grab();
            }
            else
            {
                _keyboard.Ungrab();
                _pointer.Ungrab();
            }
        }
    }

    public async Task StartEventTap(
        Action<double, double> onMouseMove,
        Action<double, double>? onMouseDelta,
        Action<KeyEvent> onKeyEvent,
        Action<MouseButtonEvent> onMouseButton,
        Action<MouseScrollEvent> onMouseScroll,
        Action? onLocalActivity = null)
    {
        _onMouseMove = onMouseMove;
        _onMouseDelta = onMouseDelta;
        _onKeyEvent = onKeyEvent;
        _onMouseButton = onMouseButton;
        _onMouseScroll = onMouseScroll;
        _onLocalActivity = onLocalActivity;

        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _eventThread = new Thread(() =>
        {
            RegisterXi2Events();
            GrabHotkeys();
            ready.TrySetResult(true);

            var xFd = NativeMethods.XConnectionNumber(_display);
            var pfd = new PollFd { Fd = xFd, Events = NativeMethods.POLLIN };

            while (_running)
            {
                // QueuedAlready touches neither the socket nor the output buffer, so a burst of
                // already-buffered motion events drains without a flush and a read apiece. Flush
                // only when the queue runs dry and we are about to block, so that anything a
                // handler buffered still reaches the server.
                if (NativeMethods.XEventsQueued(_display, NativeMethods.QueuedAlready) == 0)
                {
                    _ = NativeMethods.XFlush(_display);
                    if (NativeMethods.XEventsQueued(_display, NativeMethods.QueuedAfterReading) == 0)
                    {
                        NativeMethods.Poll(ref pfd, 1, 100);  // block up to 100ms, then check _running
                        continue;
                    }
                }

                _ = NativeMethods.XNextEvent(_display, out var ev);
                HandleEvent(ref ev);
            }
        })
        { IsBackground = true, Name = "HydraXorgEventTap" };

        _running = true;
        _eventThread.Start();
        await ready.Task;
    }

    public bool RecentresItself => false;

    // the pointer mask sees through our XGrabPointer, but has no bits past Button5 for the side buttons
    public bool SeesSwallowed(MouseButton button) => button is MouseButton.Left or MouseButton.Middle or MouseButton.Right;

    public bool AnyMouseButtonHeld()
    {
        // XQueryPointer mask_return bits 8-12 = Button1Mask through Button5Mask
        NativeMethods.XQueryPointer(_display, _rootWindow, out _, out _, out _, out _, out _, out _, out var mask);
        return (mask & 0x1F00) != 0;
    }

    public bool KeyboardCaptured => _keyboard.IsGrabbed;

    public bool AnyKeyHeld() => XorgDisplayHelper.HeldKeycodes(_display).Count > 0;

    // the keymap sees through our XGrabKeyboard; a physical key is its keycode
    public bool? KeyHeld(int physicalKey) => XorgDisplayHelper.HeldKeycodes(_display).Contains((uint)physicalKey);

    public void StopEventTap()
    {
        _running = false;
        _eventThread?.Join(TimeSpan.FromSeconds(2));
    }

    public async ValueTask DisposeAsync()
    {
        StopEventTap();
        _keyboard.Ungrab();
        _pointer.Ungrab();
        UngrabHotkeys();
        if (_cursorHidden) await ShowCursor();
        if (_inputSink != nint.Zero) _ = NativeMethods.XDestroyWindow(_display, _inputSink);
        if (_display != nint.Zero) _ = NativeMethods.XCloseDisplay(_display);
    }


    private void HandleEvent(ref XEvent ev)
    {
        // standard keyboard events from XGrabKeyboard or XGrabKey
        if (ev.Type is NativeMethods.KeyPress or NativeMethods.KeyRelease)
        {
            // without detectable auto-repeat a repeat is a KeyRelease immediately followed by a KeyPress of the same
            // keycode: peek, and skip a KeyRelease whose KeyPress is already queued
            if (!_detectableAutoRepeat && ev.Type == NativeMethods.KeyRelease && NativeMethods.XPending(_display) > 0)
            {
                NativeMethods.XPeekEvent(_display, out var next);
                if (next.Type == NativeMethods.KeyPress && next.XKeyKeycode == ev.XKeyKeycode)
                    return;
            }
            var keyEvents = _keyResolver.Resolve(ev.Type, ev.XKeyKeycode, ev.XKeyState, _display);
            if (keyEvents is not null)
                foreach (var keyEvent in keyEvents)
                    if (keyEvent is not null) _onKeyEvent?.Invoke(keyEvent);
            return;
        }

        // XI2 raw events
        if (ev.Type != NativeMethods.GenericEvent) return;
        if (ev.XCookieExtension != _xiOpcode) return;
        if (!NativeMethods.XGetEventData(_display, ref ev)) return;

        try
        {
            if (ev.XCookieEvType == NativeMethods.XI_HierarchyChanged)
            {
                _xtestSources.Clear();
                return;
            }

            // Pointer input another client injects here (XTest: a VNC server, xdotool) is someone at this display, not
            // the user's hands on the remote. During a visit none of it is forwarded, and the cursor comes home to where
            // it points; the grab has already taken the core press of a click or scroll that ends a visit, so that one is
            // lost. At home it moves the pointer but crosses no edge, or a VNC user at the edge facing a remote would be
            // bounced between the two; nor does it count as local activity. Injected keys are left alone: xcape, espanso
            // and keep-awake scripts inject them while the user works on a remote.
            if (_injectedInputReturnsHome && IsInjectedPointer(ev))
            {
                // read once: an entry landing between two reads would drop an injected click without ending the visit
                bool visiting = _isOnVirtualScreen;
                if (visiting && ev.XCookieEvType is NativeMethods.XI_RawButtonPress or NativeMethods.XI_RawMotion)
                    InjectedInput?.Invoke(XorgDisplayHelper.CursorPosition(_display, _rootWindow));
                if (visiting || ev.XCookieEvType == NativeMethods.XI_RawMotion) return;
            }

            if (ev.XCookieEvType == NativeMethods.XI_RawKeyPress && !_isOnVirtualScreen)
            {
                // grab-based KeyPress/KeyRelease only arrive when cursor is on a slave; raw key presses
                // cover the local case so activity is tracked and slaves receive pings
                _onLocalActivity?.Invoke();
                return;
            }

            if (ev.XCookieEvType == NativeMethods.XI_RawMotion)
            {
                if (_isOnVirtualScreen && _onMouseDelta != null)
                {
                    // use accelerated valuator deltas — immune to XQueryPointer/XWarpPointer race
                    ExtractValuatorDeltas(ev.XCookieData, out var adx, out var ady, out var rdx, out var rdy);
                    if (_log.IsEnabled(LogLevel.Trace))
                        _log.LogTrace("XI2 motion: accel=({Ax:F2}, {Ay:F2}) raw=({Rx:F2}, {Ry:F2})", adx, ady, rdx, rdy);
                    if (adx != 0 || ady != 0) _onMouseDelta(adx, ady);
                }
                else
                {
                    // Raw motion is grab-immune (unlike a cooked/selected event), so this is the only
                    // reliable way to track the local cursor while another app holds an active grab
                    // (window drag, drag-and-drop, a scrollbar).
                    if (XorgDisplayHelper.CursorPosition(_display, _rootWindow) is { } pos)
                        _onMouseMove?.Invoke(pos.X, pos.Y);
                }
            }
            else if (ev.XCookieEvType is NativeMethods.XI_RawButtonPress or NativeMethods.XI_RawButtonRelease)
            {
                var rawEvent = Marshal.PtrToStructure<XIRawEvent>(ev.XCookieData);
                var btn = rawEvent.Detail;
                var isDown = ev.XCookieEvType == NativeMethods.XI_RawButtonPress;

                // buttons 4-7: scroll (X11 convention: 4=up, 5=down, 6=left, 7=right)
                if (btn is >= 4 and <= 7)
                {
                    var scroll = btn switch
                    {
                        4 => new MouseScrollEvent(0, MouseScrollEvent.Notch),   // scroll up
                        5 => new MouseScrollEvent(0, -MouseScrollEvent.Notch),  // scroll down
                        6 => new MouseScrollEvent(-MouseScrollEvent.Notch, 0),  // scroll left
                        _ => new MouseScrollEvent(MouseScrollEvent.Notch, 0),   // scroll right (7)
                    };
                    if (isDown) _onMouseScroll?.Invoke(scroll);  // only fire on press (scroll has no up)
                }
                else
                {
                    _onMouseButton?.Invoke(new MouseButtonEvent(XorgDisplayHelper.ButtonOf((uint)btn), isDown));
                }
            }
        }
        finally
        {
            NativeMethods.XFreeEventData(_display, ref ev);
        }
    }

    // whether a raw pointer event came from an XTEST pointer, the device XTest injects through: each master pointer has
    // one, named "<master> XTEST pointer". Asked once per source device, on the event thread.
    private bool IsInjectedPointer(XEvent ev)
    {
        if (ev.XCookieEvType is not (NativeMethods.XI_RawButtonPress or NativeMethods.XI_RawButtonRelease or NativeMethods.XI_RawMotion))
            return false;
        var source = Marshal.PtrToStructure<XIRawEvent>(ev.XCookieData).SourceId;
        if (_xtestSources.TryGetValue(source, out var xtest)) return xtest;

        xtest = false;
        var info = NativeMethods.XIQueryDevice(_display, source, out var count);
        if (info != nint.Zero)
        {
            try
            {
                if (count > 0 && Marshal.PtrToStringUTF8(Marshal.PtrToStructure<XIDeviceInfo>(info).Name) is { } name)
                    xtest = name.EndsWith(" XTEST pointer", StringComparison.Ordinal);
            }
            finally
            {
                NativeMethods.XIFreeDeviceInfo(info);
            }
        }
        _xtestSources[source] = xtest;
        return xtest;
    }

    // runs before each XGrabKeyboard; the grab then drops the seeds released in between. a key going down in between
    // has its first repeat forwarded as a down, never a real down swallowed. both query outside the resolver's lock,
    // since Resolve takes Xlib's display lock under it.
    private void ResetHeldKeys()
    {
        var held = XorgDisplayHelper.HeldKeycodes(_display);
        _keyResolver.Reset(held);
    }

    // passive grabs for the hotkeys used from home (XorgHotkeyGrab), each in 4 variants for NumLock/CapsLock combinations;
    // a chord another client already grabs (a desktop's own shortcut) is refused, and then works only on a remote
    private void GrabHotkeys()
    {
        foreach (var key in _hotkeys)
        {
            var grabbed = XErrors.Succeeds(_display, () =>
            {
                foreach (var extra in LockModVariants())
                    _ = NativeMethods.XGrabKey(_display, key.Keycode, key.Mods | extra, _rootWindow, true,
                        NativeMethods.GrabModeAsync, NativeMethods.GrabModeAsync);
            });
            if (!grabbed && _log.IsEnabled(LogLevel.Warning))
                _log.LogWarning("A hotkey for {Action} is taken by another application, so it works only on a remote", key.Action);
        }
    }

    private void UngrabHotkeys()
    {
        foreach (var key in _hotkeys)
            foreach (var extra in LockModVariants())
                _ = NativeMethods.XUngrabKey(_display, key.Keycode, key.Mods | extra, _rootWindow);
        _ = NativeMethods.XFlush(_display);
    }

    // yields the 4 modifier variants needed for NumLock/CapsLock combinations
    private static IEnumerable<uint> LockModVariants()
    {
        yield return 0;
        yield return NativeMethods.LockMask;
        yield return NativeMethods.Mod2Mask;
        yield return NativeMethods.LockMask | NativeMethods.Mod2Mask;
    }

    private void RegisterXi2Events()
    {
        var maskHandle = GCHandle.Alloc(Xi2Mask, GCHandleType.Pinned);
        try
        {
            var xi2Mask = new XIEventMask
            {
                DeviceId = NativeMethods.XIAllMasterDevices,
                MaskLen = Xi2Mask.Length,
                Mask = maskHandle.AddrOfPinnedObject(),
            };
            _ = NativeMethods.XISelectEvents(_display, _rootWindow, ref xi2Mask, 1);
            _ = NativeMethods.XFlush(_display);
        }
        finally
        {
            maskHandle.Free();
        }

        var hierarchyHandle = GCHandle.Alloc(HierarchyMask, GCHandleType.Pinned);
        try
        {
            var hierarchyMask = new XIEventMask
            {
                DeviceId = NativeMethods.XIAllDevices,
                MaskLen = HierarchyMask.Length,
                Mask = hierarchyHandle.AddrOfPinnedObject(),
            };
            _ = NativeMethods.XISelectEvents(_display, _rootWindow, ref hierarchyMask, 1);
            _ = NativeMethods.XFlush(_display);
        }
        finally
        {
            hierarchyHandle.Free();
        }
    }

    private void RaiseInputSink()
    {
        _ = NativeMethods.XMapWindow(_display, _inputSink);
        _ = NativeMethods.XRaiseWindow(_display, _inputSink);
    }

    // full-screen InputOnly OverrideRedirect window — absorbs pointer hover events while on virtual screen,
    // and serves as the grab window for XGrabKeyboard
    private nint CreateInputSink()
    {
        var screen = NativeMethods.XDefaultScreen(_display);
        var w = (uint)NativeMethods.XDisplayWidth(_display, screen);
        var h = (uint)NativeMethods.XDisplayHeight(_display, screen);
        var attrs = new XSetWindowAttributes { OverrideRedirect = 1 };
        return NativeMethods.XCreateWindow(
            _display, _rootWindow,
            0, 0, w, h,
            0, 0, NativeMethods.InputOnly,
            nint.Zero, NativeMethods.CWOverrideRedirect,
            ref attrs);
    }

    private int DetectXi2()
    {
        if (!NativeMethods.XQueryExtension(_display, "XInputExtension", out var opcode, out _, out _))
            throw new InvalidOperationException("XInput2 extension not available — Xorg with XInput2 is required");
        return opcode;
    }

    // reads both accelerated (valuators.values) and raw (raw_values) deltas from XIRawEvent.
    // mask bit 0 = X axis, bit 1 = Y axis; each array packed consecutively for set bits.
    private static void ExtractValuatorDeltas(nint cookieData, out double accelDx, out double accelDy, out double rawDx, out double rawDy)
    {
        accelDx = accelDy = rawDx = rawDy = 0;
        var ev = Marshal.PtrToStructure<XIRawEvent>(cookieData);
        if (ev.ValuatorsMaskLen < 1 || ev.ValuatorsMask == nint.Zero) return;
        var maskByte = Marshal.ReadByte(ev.ValuatorsMask);
        var xPresent = (maskByte & 0x01) != 0;
        var yPresent = (maskByte & 0x02) != 0;
        int idx = 0;
        if (xPresent)
        {
            if (ev.ValuatorValues != nint.Zero) accelDx = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(ev.ValuatorValues, 0));
            if (ev.RawValues != nint.Zero) rawDx = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(ev.RawValues, 0));
            idx++;
        }
        if (yPresent)
        {
            if (ev.ValuatorValues != nint.Zero) accelDy = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(ev.ValuatorValues, idx * 8));
            if (ev.RawValues != nint.Zero) rawDy = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(ev.RawValues, idx * 8));
        }
    }
}
