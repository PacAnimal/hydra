using System.Runtime.InteropServices;
using Hydra.Keyboard;
using Hydra.Screen;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Linux;

public class XorgInputHandler : IPlatformInput
{
    private readonly ILogger<XorgInputHandler> _log;
    private readonly nint _display;
    private readonly nint _rootWindow;
    private readonly nint _blankCursor;
    private readonly int _xiOpcode;

    private Thread? _eventThread;
    private volatile bool _running;
    private Action<double, double>? _onMouseMove;
    private Action<KeyEvent>? _onKeyEvent;
    private bool _cursorHidden;
    private bool _isOnVirtualScreen;

    // XI2 mask bytes for: XI_RawKeyPress(13), XI_RawKeyRelease(14), XI_RawMotion(17).
    // XISetMask(mask, n) = mask[n>>3] |= 1 << (n&7)
    private static readonly byte[] Xi2Mask =
    [
        0,
        (1 << (NativeMethods.XI_RawKeyPress & 7)) | (1 << (NativeMethods.XI_RawKeyRelease & 7)), // byte 1: bits 5+6
        (1 << (NativeMethods.XI_RawMotion & 7)),                                                   // byte 2: bit 1
        0,
    ];

    public XorgInputHandler(ILogger<XorgInputHandler> log)
    {
        _log = log;

        // must be first X11 call in the process
        _ = NativeMethods.XInitThreads();

        _display = NativeMethods.XOpenDisplay(null);
        if (_display == nint.Zero)
            throw new InvalidOperationException("XOpenDisplay failed — is DISPLAY set?");

        _rootWindow = NativeMethods.XDefaultRootWindow(_display);
        _blankCursor = CreateBlankCursor();
        _xiOpcode = DetectXI2();
    }

    public ScreenRect GetPrimaryScreenBounds()
    {
        var screen = NativeMethods.XDefaultScreen(_display);
        var w = NativeMethods.XDisplayWidth(_display, screen);
        var h = NativeMethods.XDisplayHeight(_display, screen);
        return new ScreenRect("main", 0, 0, w, h, false);
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
        _ = NativeMethods.XDefineCursor(_display, _rootWindow, _blankCursor);
        _ = NativeMethods.XFlush(_display);
        _cursorHidden = true;
    }

    public void ShowCursor()
    {
        if (!_cursorHidden) return;
        _ = NativeMethods.XUndefineCursor(_display, _rootWindow);
        _ = NativeMethods.XFlush(_display);
        _cursorHidden = false;
    }

    // when true, grab the keyboard so apps can't receive key events.
    // XI2 raw events bypass grabs, so our listener still sees everything.
    public bool IsOnVirtualScreen
    {
        get => _isOnVirtualScreen;
        set
        {
            if (_isOnVirtualScreen == value) return;
            _isOnVirtualScreen = value;

            if (value)
                GrabKeyboard();
            else
                UngrabKeyboard();
        }
    }

    public void StartEventTap(Action<double, double> onMouseMove, Action<KeyEvent> onKeyEvent)
    {
        _onMouseMove = onMouseMove;
        _onKeyEvent = onKeyEvent;

        var ready = new ManualResetEventSlim(false);

        _eventThread = new Thread(() =>
        {
            RegisterXI2Events();
            ready.Set();

            while (_running)
            {
                if (NativeMethods.XPending(_display) > 0)
                {
                    _ = NativeMethods.XNextEvent(_display, out var ev);
                    HandleEvent(ref ev);
                }
                else
                    Thread.Sleep(1);
            }
        })
        { IsBackground = true, Name = "HydraXorgEventTap" };

        _running = true;
        _eventThread.Start();
        ready.Wait();
    }

    public void StopEventTap()
    {
        _running = false;
        _eventThread?.Join(TimeSpan.FromSeconds(2));
    }

    public void Dispose()
    {
        StopEventTap();
        if (_cursorHidden) ShowCursor();
        if (_isOnVirtualScreen) UngrabKeyboard();
        if (_blankCursor != nint.Zero) _ = NativeMethods.XFreeCursor(_display, _blankCursor);
        if (_display != nint.Zero) _ = NativeMethods.XCloseDisplay(_display);
        GC.SuppressFinalize(this);
    }

    private void HandleEvent(ref XEvent ev)
    {
        if (ev.Type != NativeMethods.GenericEvent) return;
        if (ev.XCookieExtension != _xiOpcode) return;
        if (!NativeMethods.XGetEventData(_display, ref ev)) return;

        try
        {
            switch (ev.XCookieEvType)
            {
                case NativeMethods.XI_RawMotion:
                    // use XQueryPointer for absolute position (matches deskflow approach)
                    _ = NativeMethods.XQueryPointer(_display, _rootWindow,
                        out _, out _, out var rootX, out var rootY, out _, out _, out _);
                    _onMouseMove?.Invoke(rootX, rootY);
                    break;

                case NativeMethods.XI_RawKeyPress:
                case NativeMethods.XI_RawKeyRelease:
                    // XIRawEvent.detail is the keycode; on 64-bit LP64 it sits at offset 56:
                    // type(4)+pad(4)+serial(8)+send_event(4)+pad(4)+display*(8)+extension(4)+evtype(4)+time(8)+deviceid(4)+sourceid(4) = 56
                    var keycode = (uint)Marshal.ReadInt32(ev.XCookieData, 56);
                    var keyEvent = XorgKeyResolver.Resolve(ev.XCookieEvType, keycode, _display);
                    if (keyEvent is not null)
                        _onKeyEvent?.Invoke(keyEvent);
                    break;
            }
        }
        finally
        {
            NativeMethods.XFreeEventData(_display, ref ev);
        }
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

    private void GrabKeyboard()
    {
        var result = NativeMethods.XGrabKeyboard(
            _display, _rootWindow, false,
            NativeMethods.GrabModeAsync, NativeMethods.GrabModeAsync,
            NativeMethods.CurrentTime);

        if (result != NativeMethods.GrabSuccess)
            _log.LogWarning("XGrabKeyboard failed (result={Result}) — keyboard events may reach apps", result);

        _ = NativeMethods.XFlush(_display);
    }

    private void UngrabKeyboard()
    {
        _ = NativeMethods.XUngrabKeyboard(_display, NativeMethods.CurrentTime);
        _ = NativeMethods.XFlush(_display);
    }

    // creates a 1x1 blank pixmap cursor used to hide the OS cursor
    private unsafe nint CreateBlankCursor()
    {
        byte data = 0;
        var pixmap = NativeMethods.XCreateBitmapFromData(_display, _rootWindow, &data, 1, 1);
        var color = default(XColor);
        var cursor = NativeMethods.XCreatePixmapCursor(_display, pixmap, pixmap, ref color, ref color, 0, 0);
        _ = NativeMethods.XFreePixmap(_display, pixmap);
        return cursor;
    }

    private int DetectXI2()
    {
        if (!NativeMethods.XQueryExtension(_display, "XInputExtension", out var opcode, out _, out _))
            throw new InvalidOperationException("XInput2 extension not available — Xorg with XInput2 is required");
        return opcode;
    }
}
