using System.Runtime.InteropServices;
using Hydra.Screen;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.MacOs;

public class MacInputHandler(ILogger<MacInputHandler> log) : IPlatformInput
{
    private readonly uint _display = NativeMethods.CGMainDisplayID();
    private readonly nint _cfBooleanTrue = GetCFBooleanTrue();

    // stored as fields to prevent GC collection while the tap is active
    private CGEventTapCallBack? _tapCallback;
    private Thread? _tapThread;
    private nint _runLoop;
    private nint _tapPort;
    private nint _runLoopSource;
    private Action<double, double>? _onMouseMove;
    private bool _cursorHidden;
    public bool IsOnVirtualScreen { get; set; }

    public ScreenRect GetPrimaryScreenBounds()
    {
        var bounds = NativeMethods.CGDisplayBounds(_display);
        return new ScreenRect(
            "main",
            (int)bounds.Origin.X, (int)bounds.Origin.Y,
            (int)bounds.Size.X, (int)bounds.Size.Y,
            false);
    }

    public bool IsAccessibilityTrusted() => NativeMethods.AXIsProcessTrusted();

    public void WarpCursor(int x, int y)
    {
        // SYSLIB1054: return value intentionally discarded (CG error codes are advisory only)
        _ = NativeMethods.CGWarpMouseCursorPosition(new CGPoint { X = x, Y = y });
    }

    public void HideCursor()
    {
        if (_cursorHidden) return;

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
        _ = NativeMethods.CGDisplayShowCursor(_display);
        _ = NativeMethods.CGAssociateMouseAndMouseCursorPosition(true);
        NativeMethods.CGSetLocalEventsSuppressionInterval(0.0);
        _cursorHidden = false;
    }

    public void StartEventTap(Action<double, double> onMouseMove)
    {
        _onMouseMove = onMouseMove;

        // callback must be stored as field -- will crash if collected
        _tapCallback = TapCallback;

        var ready = new ManualResetEventSlim(false);

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

        // swallow all non-mouse events while on virtual screen (synergy: return nullptr when off-screen)
        return IsOnVirtualScreen ? nint.Zero : eventRef;
    }

    // reads kCFRunLoopCommonModes symbol pointer from CoreFoundation
    private static nint GetCFRunLoopCommonModes()
    {
        var lib = NativeLibrary.Load("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation");
        var export = NativeLibrary.GetExport(lib, "kCFRunLoopCommonModes");
        return Marshal.ReadIntPtr(export);
    }

    // reads kCFBooleanTrue symbol pointer from CoreFoundation
    private static nint GetCFBooleanTrue()
    {
        var lib = NativeLibrary.Load("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation");
        var export = NativeLibrary.GetExport(lib, "kCFBooleanTrue");
        return Marshal.ReadIntPtr(export);
    }
}
