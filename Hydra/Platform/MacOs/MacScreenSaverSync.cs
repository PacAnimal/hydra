using System.Diagnostics;
using Cathedral.Utils;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.MacOs;

// always running as a hosted service; fires events when screensaver/lock state changes.
public sealed class MacScreenSaverSync : SimpleHostedService, IScreenSaverSync
{
    // distributed notification names posted by ScreenSaverEngine
    private const string DidStart = "com.apple.screensaver.didstart";
    private const string DidStop = "com.apple.screensaver.didstop";

    private const string AssertionType = "PreventUserIdleDisplaySleep";
    private const string AssertionReason = "Hydra screensaver sync: controlled by master";

    private readonly ILogger<MacScreenSaverSync> _log;
    private CFNotificationCallback? _callback;  // keep-alive to prevent GC
    private nint _center;
    private uint _assertionId;
    private bool _wasLocked;

    public event Action? ScreensaverActivated;
    public event Action? ScreensaverDeactivated;
    public event Action? ScreenLocked;
    public event Action? ScreenUnlocked;

    public MacScreenSaverSync(ILogger<MacScreenSaverSync> log) : base(log, TimeSpan.FromSeconds(1))
    {
        _log = log;
        RegisterScreensaverNotifications();
    }

    private void RegisterScreensaverNotifications()
    {
        _center = NativeMethods.CFNotificationCenterGetDistributedCenter();
        if (_center == nint.Zero)
        {
            _log.LogWarning("Failed to get CFNotificationCenter — screensaver watching disabled");
            return;
        }

        _log.LogInformation("Watching for screensaver notifications");

        _callback = (_, _, name, _, _) =>
        {
            // resolve CFStringRef name to a managed string for comparison
            var str = NativeMethods.CfStringToManaged(name) ?? "";
            if (str == DidStart)
            {
                _log.LogInformation("Screensaver started (notification received)");
                ScreensaverActivated?.Invoke();
            }
            else if (str == DidStop)
            {
                _log.LogInformation("Screensaver stopped (notification received)");
                ScreensaverDeactivated?.Invoke();
            }
        };

        var nameStart = NativeMethods.MakeNsString(DidStart);
        var nameStop = NativeMethods.MakeNsString(DidStop);

        // use a stable observer pointer (1 / 2) to distinguish the two registrations on removal
        NativeMethods.CFNotificationCenterAddObserver(_center, 1, _callback, nameStart, nint.Zero,
            NativeMethods.CFNotificationSuspensionBehaviorDeliverImmediately);
        NativeMethods.CFNotificationCenterAddObserver(_center, 2, _callback, nameStop, nint.Zero,
            NativeMethods.CFNotificationSuspensionBehaviorDeliverImmediately);

        NativeMethods.CFRelease(nameStart);
        NativeMethods.CFRelease(nameStop);
    }

    protected override Task OnShutdown(CancellationToken cancel)
    {
        if (_center == nint.Zero) return Task.CompletedTask;
        _log.LogInformation("Stopped watching for screensaver notifications");

        var nameStart = NativeMethods.MakeNsString(DidStart);
        var nameStop = NativeMethods.MakeNsString(DidStop);
        NativeMethods.CFNotificationCenterRemoveObserver(_center, 1, nameStart, nint.Zero);
        NativeMethods.CFNotificationCenterRemoveObserver(_center, 2, nameStop, nint.Zero);
        NativeMethods.CFRelease(nameStart);
        NativeMethods.CFRelease(nameStop);

        _callback = null;
        _center = nint.Zero;
        return Task.CompletedTask;
    }

    // reads IOConsoleUsers[0].CGSSessionScreenIsLocked from the IORegistry root entry.
    // this reflects actual password-required lock state, not just screensaver activation.
    private static bool IsScreenLocked()
    {
        uint rootEntry = 0;
        nint consoleUsersKey = nint.Zero;
        nint consoleUsersArray = nint.Zero;
        nint screenLockedKey = nint.Zero;

        try
        {
            rootEntry = NativeMethods.IORegistryGetRootEntry(0);
            if (rootEntry == 0) return false;

            consoleUsersKey = NativeMethods.MakeNsString("IOConsoleUsers");
            consoleUsersArray = NativeMethods.IORegistryEntryCreateCFProperty(rootEntry, consoleUsersKey, nint.Zero, 0);
            if (consoleUsersArray == nint.Zero) return false;

            if (NativeMethods.CFArrayGetCount(consoleUsersArray) == 0) return false;

            var userDict = NativeMethods.CFArrayGetValueAtIndex(consoleUsersArray, 0);
            if (userDict == nint.Zero) return false;

            screenLockedKey = NativeMethods.MakeNsString("CGSSessionScreenIsLocked");
            var lockedRef = NativeMethods.CFDictionaryGetValue(userDict, screenLockedKey);
            if (lockedRef == nint.Zero) return false;

            return NativeMethods.CFBooleanGetValue(lockedRef) != 0;
        }
        finally
        {
            if (consoleUsersKey != nint.Zero) NativeMethods.CFRelease(consoleUsersKey);
            if (screenLockedKey != nint.Zero) NativeMethods.CFRelease(screenLockedKey);
            if (consoleUsersArray != nint.Zero) NativeMethods.CFRelease(consoleUsersArray);
            if (rootEntry != 0) _ = NativeMethods.IOObjectRelease(rootEntry);
        }
    }

    public void LockScreen()
    {
        _log.LogInformation("Locking screen (ctrl+cmd+q)");
        var src = NativeMethods.CGEventSourceCreate(NativeMethods.KCGEventSourceStateCombinedSessionState);
        const ulong flags = NativeMethods.KCGEventFlagMaskControl | NativeMethods.KCGEventFlagMaskCommand;
        const ushort qKeyCode = 12;  // Q key
        var down = NativeMethods.CGEventCreateKeyboardEvent(src, qKeyCode, true);
        var up = NativeMethods.CGEventCreateKeyboardEvent(src, qKeyCode, false);
        if (down != nint.Zero)
        {
            NativeMethods.CGEventSetFlags(down, flags);
            NativeMethods.CGEventPost(NativeMethods.KCGHidEventTap, down);
            NativeMethods.CFRelease(down);
        }
        if (up != nint.Zero)
        {
            NativeMethods.CGEventSetFlags(up, flags);
            NativeMethods.CGEventPost(NativeMethods.KCGHidEventTap, up);
            NativeMethods.CFRelease(up);
        }
        if (src != nint.Zero) NativeMethods.CFRelease(src);
    }

    public void Activate()
    {
        _log.LogInformation("Activating screensaver");
        // launch ScreenSaverEngine directly
        try { Process.Start("open", ["-a", "ScreenSaverEngine"]); }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to launch ScreenSaverEngine"); }
    }

    public void Deactivate()
    {
        _log.LogInformation("Deactivating screensaver");
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
            _log.LogDebug("IOPMAssertion already active (id={Id})", _assertionId);
            return;
        }
        var typeStr = NativeMethods.MakeNsString(AssertionType);
        var nameStr = NativeMethods.MakeNsString(AssertionReason);
        var result = NativeMethods.IOPMAssertionCreateWithName(typeStr, NativeMethods.KIOPMAssertionLevelOn, nameStr, out _assertionId);
        NativeMethods.CFRelease(typeStr);
        NativeMethods.CFRelease(nameStr);
        if (result == 0)
            _log.LogDebug("IOPMAssertion created (id={Id})", _assertionId);
        else
            _log.LogWarning("IOPMAssertionCreateWithName failed (result={Result})", result);
    }

    public void Restore()
    {
        if (_assertionId == 0) return;
        var result = NativeMethods.IOPMAssertionRelease(_assertionId);
        _log.LogDebug("IOPMAssertion released (id={Id}, result={Result})", _assertionId, result);
        _assertionId = 0;
    }

    public void ResetIdleTimer()
    {
        var nameStr = NativeMethods.MakeNsString("Hydra: user active on remote screen");
        _ = NativeMethods.IOPMAssertionDeclareUserActivity(nameStr, 0, out _);
        NativeMethods.CFRelease(nameStr);
    }

    public TimeSpan? GetIdleTime()
    {
        var secs = NativeMethods.CGEventSourceSecondsSinceLastEventType(NativeMethods.KCGEventSourceStateCombinedSessionState, NativeMethods.KCGAnyInputEventType);
        return TimeSpan.FromSeconds(secs);
    }

    protected override Task Execute(CancellationToken cancel)
    {
        var isLocked = IsScreenLocked();
        if (isLocked && !_wasLocked)
        {
            _log.LogInformation("Screen locked (IORegistry detected)");
            ScreenLocked?.Invoke();
        }
        else if (!isLocked && _wasLocked)
        {
            _log.LogInformation("Screen unlocked (IORegistry detected)");
            ScreenUnlocked?.Invoke();
        }
        _wasLocked = isLocked;
        return Task.CompletedTask;
    }

}
