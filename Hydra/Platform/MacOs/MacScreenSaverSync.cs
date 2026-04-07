using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.MacOs;

public sealed partial class MacScreenSaverSync(ILogger<MacScreenSaverSync> log) : IScreenSaverSync
{
    // distributed notification names posted by ScreenSaverEngine
    private const string DidStart = "com.apple.screensaver.didstart";
    private const string DidStop = "com.apple.screensaver.didstop";

    private const string AssertionType = "PreventUserIdleDisplaySleep";
    private const string AssertionReason = "Hydra screensaver sync: controlled by master";

    private CFNotificationCallback? _callback;  // keep-alive to prevent GC
    private nint _center;
    private uint _assertionId;

    public void StartWatching(Action onActivated, Action onDeactivated)
    {
        _center = NativeMethods.CFNotificationCenterGetDistributedCenter();
        if (_center == nint.Zero)
        {
            log.LogWarning("Failed to get CFNotificationCenter — screensaver watching disabled");
            return;
        }

        log.LogInformation("Watching for screensaver notifications");

        _callback = (_, _, name, _, _) =>
        {
            // resolve CFStringRef name to a managed string for comparison
            var str = CfStringToString(name);
            if (str == DidStart)
            {
                log.LogInformation("Screensaver started (notification received)");
                onActivated();
            }
            else if (str == DidStop)
            {
                log.LogInformation("Screensaver stopped (notification received)");
                onDeactivated();
            }
        };

        var nameStart = NativeMethods.CFStringCreateWithCString(nint.Zero, DidStart, NativeMethods.KCFStringEncodingUtf8);
        var nameStop = NativeMethods.CFStringCreateWithCString(nint.Zero, DidStop, NativeMethods.KCFStringEncodingUtf8);

        // use a stable observer pointer (1 / 2) to distinguish the two registrations on removal
        NativeMethods.CFNotificationCenterAddObserver(_center, 1, _callback, nameStart, nint.Zero,
            NativeMethods.CFNotificationSuspensionBehaviorDeliverImmediately);
        NativeMethods.CFNotificationCenterAddObserver(_center, 2, _callback, nameStop, nint.Zero,
            NativeMethods.CFNotificationSuspensionBehaviorDeliverImmediately);

        NativeMethods.CFRelease(nameStart);
        NativeMethods.CFRelease(nameStop);
    }

    public void StopWatching()
    {
        if (_center == nint.Zero) return;
        log.LogInformation("Stopped watching for screensaver notifications");

        var nameStart = NativeMethods.CFStringCreateWithCString(nint.Zero, DidStart, NativeMethods.KCFStringEncodingUtf8);
        var nameStop = NativeMethods.CFStringCreateWithCString(nint.Zero, DidStop, NativeMethods.KCFStringEncodingUtf8);
        NativeMethods.CFNotificationCenterRemoveObserver(_center, 1, nameStart, nint.Zero);
        NativeMethods.CFNotificationCenterRemoveObserver(_center, 2, nameStop, nint.Zero);
        NativeMethods.CFRelease(nameStart);
        NativeMethods.CFRelease(nameStop);

        _callback = null;
        _center = nint.Zero;
    }

    public void Activate()
    {
        log.LogInformation("Activating screensaver");
        // launch ScreenSaverEngine directly
        try { Process.Start("open", ["-a", "ScreenSaverEngine"]); }
        catch (Exception ex) { log.LogWarning(ex, "Failed to launch ScreenSaverEngine"); }
    }

    public void Deactivate()
    {
        log.LogInformation("Deactivating screensaver");
        // a synthetic mouse move dismisses the screensaver reliably
        var src = NativeMethods.CGEventSourceCreate(NativeMethods.KCGEventSourceStateCombinedSessionState);
        var evt = NativeMethods.CGEventCreateMouseEvent(src, NativeMethods.KCGEventMouseMoved,
            new CGPoint { X = 0, Y = 0 }, 0);
        if (evt != nint.Zero)
        {
            NativeMethods.CGEventPost(NativeMethods.KCGHidEventTap, evt);
            NativeMethods.CFRelease(evt);
        }
        if (src != nint.Zero) NativeMethods.CFRelease(src);
    }

    public void Suppress()
    {
        if (_assertionId != 0)
        {
            log.LogDebug("IOPMAssertion already active (id={Id})", _assertionId);
            return;
        }
        var typeStr = NativeMethods.CFStringCreateWithCString(nint.Zero, AssertionType, NativeMethods.KCFStringEncodingUtf8);
        var nameStr = NativeMethods.CFStringCreateWithCString(nint.Zero, AssertionReason, NativeMethods.KCFStringEncodingUtf8);
        var result = NativeMethods.IOPMAssertionCreateWithName(typeStr, NativeMethods.KIOPMAssertionLevelOn, nameStr, out _assertionId);
        NativeMethods.CFRelease(typeStr);
        NativeMethods.CFRelease(nameStr);
        if (result == 0)
            log.LogDebug("IOPMAssertion created (id={Id})", _assertionId);
        else
            log.LogWarning("IOPMAssertionCreateWithName failed (result={Result})", result);
    }

    public void Restore()
    {
        if (_assertionId == 0) return;
        var result = NativeMethods.IOPMAssertionRelease(_assertionId);
        log.LogDebug("IOPMAssertion released (id={Id}, result={Result})", _assertionId, result);
        _assertionId = 0;
    }

    private static unsafe string CfStringToString(nint cfString)
    {
        if (cfString == nint.Zero) return "";
        // use CFStringGetCString into a stack buffer — fine for short notification names
        var buf = stackalloc byte[256];
        if (CfStringGetCString(cfString, buf, 256, NativeMethods.KCFStringEncodingUtf8))
            return Marshal.PtrToStringUTF8((nint)buf) ?? "";
        return "";
    }

    [LibraryImport(
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", EntryPoint = "CFStringGetCString")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CfStringGetCString(nint theString, byte* buffer, nint bufferSize, uint encoding);
}
