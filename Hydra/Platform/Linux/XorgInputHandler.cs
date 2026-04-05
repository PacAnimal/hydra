using System.Runtime.InteropServices;
using Cathedral.Utils;
using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Screen;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Linux;

public sealed class XorgInputHandler : IPlatformInput
{
    private readonly ILogger<XorgInputHandler> _log;
    private readonly nint _display;
    private readonly nint _rootWindow;
    private readonly nint _inputSink;
    private readonly int _xiOpcode;
    private readonly int _lockKeycode;
    private readonly XorgKeyResolver _keyResolver = new();

    private Thread? _eventThread;
    private volatile bool _running;
    private Action<double, double>? _onMouseMove;
    private Action<KeyEvent>? _onKeyEvent;
    private Action<MouseButtonEvent>? _onMouseButton;
    private Action<MouseScrollEvent>? _onMouseScroll;
    private bool _cursorHidden;
    private readonly Toggle _isOnVirtualScreen = new();
    private bool _keyboardGrabbed;

    // XI2 event mask for raw button press/release (15, 16) and raw motion (17).
    // XISetMask(mask, n) = mask[n>>3] |= 1 << (n&7)
    private static readonly byte[] Xi2Mask =
    [
        0,
        (1 << (NativeMethods.XI_RawButtonPress & 7)),   // byte 1: bit 7 (event 15)
        (1 << (NativeMethods.XI_RawButtonRelease & 7)) | (1 << (NativeMethods.XI_RawMotion & 7)),  // byte 2: bits 0+1 (events 16, 17)
        0,
    ];

    // Ctrl+Alt+Super+L — baseMods = ControlMask|Mod1Mask|Mod4Mask
    private static readonly uint LockBaseMods = NativeMethods.ControlMask | NativeMethods.Mod1Mask | NativeMethods.Mod4Mask;

    public XorgInputHandler(ILogger<XorgInputHandler> log)
    {
        _log = log;

        // must be first X11 call in the process
        _ = NativeMethods.XInitThreads();

        _display = NativeMethods.XOpenDisplay(null);
        if (_display == nint.Zero)
            throw new InvalidOperationException("XOpenDisplay failed — is DISPLAY set?");

        _rootWindow = NativeMethods.XDefaultRootWindow(_display);
        _inputSink = CreateInputSink();
        _xiOpcode = DetectXI2();
        _lockKeycode = (int)NativeMethods.XKeysymToKeycode(_display, 0x006C); // XK_l
    }


    // X11 requires no special accessibility permission (unlike macOS)
    public bool IsAccessibilityTrusted() => true;

    public void WarpCursor(int x, int y)
    {
        // SYSLIB1054: return value intentionally discarded (X error codes are advisory only)
        _ = NativeMethods.XWarpPointer(_display, nint.Zero, _rootWindow, 0, 0, 0, 0, x, y);
        _ = NativeMethods.XFlush(_display);
    }

    public void HideCursor()
    {
        if (_cursorHidden) return;
        _ = NativeMethods.XMapWindow(_display, _inputSink);
        _ = NativeMethods.XRaiseWindow(_display, _inputSink);
        NativeMethods.XFixesHideCursor(_display, _rootWindow);
        _ = NativeMethods.XFlush(_display);
        _cursorHidden = true;
    }

    public void ShowCursor()
    {
        if (!_cursorHidden) return;
        _ = NativeMethods.XUnmapWindow(_display, _inputSink);
        NativeMethods.XFixesShowCursor(_display, _rootWindow);
        _ = NativeMethods.XFlush(_display);
        _cursorHidden = false;
    }

    public bool IsOnVirtualScreen
    {
        get => _isOnVirtualScreen;
        set
        {
            _isOnVirtualScreen.TrySet(value);
            if (value)
                GrabKeyboard();
            else
                UngrabKeyboard();
        }
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

        using var ready = new ManualResetEventSlim(false);

        _eventThread = new Thread(() =>
        {
            RegisterXI2Events();
            GrabLockHotkey();
            ready.Set();

            var xFd = NativeMethods.XConnectionNumber(_display);
            var pfd = new PollFd { Fd = xFd, Events = NativeMethods.POLLIN };

            while (_running)
            {
                if (NativeMethods.XPending(_display) > 0)
                {
                    _ = NativeMethods.XNextEvent(_display, out var ev);
                    HandleEvent(ref ev);
                }
                else
                    NativeMethods.poll(ref pfd, 1, 100);  // block up to 100ms, then check _running
            }
        })
        { IsBackground = true, Name = "HydraXorgEventTap" };

        _running = true;
        _eventThread.Start();
        ready.Wait();
    }

    public KeyRepeatSettings GetKeyRepeatSettings()
    {
        if (NativeMethods.XkbGetAutoRepeatRate(_display, NativeMethods.XkbUseCoreKbd, out var delay, out var interval))
            return new KeyRepeatSettings((int)delay, (int)interval);
        return new KeyRepeatSettings(500, 33);
    }

    public void StopEventTap()
    {
        _running = false;
        _eventThread?.Join(TimeSpan.FromSeconds(2));
    }

    public void Dispose()
    {
        StopEventTap();
        UngrabKeyboard();
        UngrabLockHotkey();
        if (_cursorHidden) ShowCursor();
        if (_inputSink != nint.Zero) _ = NativeMethods.XDestroyWindow(_display, _inputSink);
        if (_display != nint.Zero) _ = NativeMethods.XCloseDisplay(_display);
        GC.SuppressFinalize(this);
    }

    private void HandleEvent(ref XEvent ev)
    {
        // standard keyboard events from XGrabKeyboard or XGrabKey
        if (ev.Type is NativeMethods.KeyPress or NativeMethods.KeyRelease)
        {
            // X11 auto-repeat emits KeyRelease immediately followed by KeyPress for the same keycode.
            // detect this by peeking: if the next queued event is a KeyPress with the same keycode, skip this KeyRelease.
            if (ev.Type == NativeMethods.KeyRelease && NativeMethods.XPending(_display) > 0)
            {
                NativeMethods.XPeekEvent(_display, out var next);
                if (next.Type == NativeMethods.KeyPress && next.XKeyKeycode == ev.XKeyKeycode)
                    return;
            }
            var keyEvent = _keyResolver.Resolve(ev.Type, ev.XKeyKeycode, ev.XKeyState, _display);
            if (keyEvent is not null)
                _onKeyEvent?.Invoke(keyEvent);
            return;
        }

        // XI2 raw events (motion only)
        if (ev.Type != NativeMethods.GenericEvent) return;
        if (ev.XCookieExtension != _xiOpcode) return;
        if (!NativeMethods.XGetEventData(_display, ref ev)) return;

        try
        {
            if (ev.XCookieEvType == NativeMethods.XI_RawMotion)
            {
                // use XQueryPointer for absolute position (matches deskflow approach)
                _ = NativeMethods.XQueryPointer(_display, _rootWindow,
                    out _, out _, out var rootX, out var rootY, out _, out _, out _);
                _onMouseMove?.Invoke(rootX, rootY);
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
                        4 => new MouseScrollEvent(0, 120),   // scroll up
                        5 => new MouseScrollEvent(0, -120),  // scroll down
                        6 => new MouseScrollEvent(-120, 0),  // scroll left
                        7 => new MouseScrollEvent(120, 0),   // scroll right
                        _ => default,
                    };
                    if (isDown) _onMouseScroll?.Invoke(scroll);  // only fire on press (scroll has no up)
                }
                else
                {
                    var button = btn switch
                    {
                        1 => MouseButton.Left,
                        2 => MouseButton.Middle,
                        3 => MouseButton.Right,
                        8 => MouseButton.Extra1,
                        _ => MouseButton.Extra2,
                    };
                    _onMouseButton?.Invoke(new MouseButtonEvent(button, isDown));
                }
            }
        }
        finally
        {
            NativeMethods.XFreeEventData(_display, ref ev);
        }
    }

    private void GrabKeyboard()
    {
        if (_keyboardGrabbed) return;
        _ = NativeMethods.XMapWindow(_display, _inputSink);
        _ = NativeMethods.XRaiseWindow(_display, _inputSink);
        var result = NativeMethods.XGrabKeyboard(_display, _inputSink, true,
            NativeMethods.GrabModeAsync, NativeMethods.GrabModeAsync, NativeMethods.CurrentTime);
        if (result == NativeMethods.GrabSuccess)
            _keyboardGrabbed = true;
        else
            _log.LogWarning("XGrabKeyboard failed (result={Result})", result);
        _ = NativeMethods.XFlush(_display);
    }

    private void UngrabKeyboard()
    {
        if (!_keyboardGrabbed) return;
        _ = NativeMethods.XUngrabKeyboard(_display, NativeMethods.CurrentTime);
        _ = NativeMethods.XFlush(_display);
        _keyboardGrabbed = false;
    }

    // passive grab for Ctrl+Alt+Super+L — 4 variants for NumLock/CapsLock combinations
    private void GrabLockHotkey()
    {
        if (_lockKeycode == 0) return;
        foreach (var extra in LockModVariants())
            _ = NativeMethods.XGrabKey(_display, _lockKeycode, LockBaseMods | extra, _rootWindow, true,
                NativeMethods.GrabModeAsync, NativeMethods.GrabModeAsync);
        _ = NativeMethods.XFlush(_display);
    }

    private void UngrabLockHotkey()
    {
        if (_lockKeycode == 0) return;
        foreach (var extra in LockModVariants())
            _ = NativeMethods.XUngrabKey(_display, _lockKeycode, LockBaseMods | extra, _rootWindow);
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

    private void RegisterXI2Events()
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

    private int DetectXI2()
    {
        if (!NativeMethods.XQueryExtension(_display, "XInputExtension", out var opcode, out _, out _))
            throw new InvalidOperationException("XInput2 extension not available — Xorg with XInput2 is required");
        return opcode;
    }
}
