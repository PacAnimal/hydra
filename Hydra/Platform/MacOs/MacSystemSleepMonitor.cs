using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.MacOs;

[SupportedOSPlatform("macos")]
internal sealed class MacSystemSleepMonitor : ThreadedSleepMonitor
{
    private readonly NativeMethods.IOServiceInterestCallback _callback;
    private nint _runLoop;
    private uint _kernelPort;

    public MacSystemSleepMonitor(SystemSleepCoordinator coordinator, ILogger<MacSystemSleepMonitor> log) : base(coordinator, log)
    {
        _callback = OnPowerMessage;
    }

    protected override string NotificationSource => "IOKit system sleep notifications";
    protected override string MonitorName => "macOS system sleep monitor";

    protected override void RunLoop(TaskCompletionSource<bool> ready)
    {
        uint notifier = 0;
        nint notificationPort = nint.Zero;
        try
        {
            var kernelPort = NativeMethods.IORegisterForSystemPower(
                nint.Zero, out notificationPort, _callback, out notifier);
            _kernelPort = kernelPort;
            if (kernelPort == 0 || notificationPort == nint.Zero)
            {
                ready.TrySetResult(false);
                return;
            }

            var source = NativeMethods.IONotificationPortGetRunLoopSource(notificationPort);
            if (source == nint.Zero)
            {
                ready.TrySetResult(false);
                return;
            }

            var runLoop = NativeMethods.CFRunLoopGetCurrent();
            NativeMethods.CFRunLoopAddSource(runLoop, source, NativeMethods.KCFRunLoopCommonModes);
            if (Stopping)
            {
                ready.TrySetResult(false);
                return;
            }
            Interlocked.Exchange(ref _runLoop, runLoop);
            // StopAsync may have checked _runLoop immediately before publication. Re-check after
            // publishing so the worker cannot enter CFRunLoopRun after shutdown has already begun.
            if (Stopping)
            {
                Interlocked.Exchange(ref _runLoop, nint.Zero);
                ready.TrySetResult(false);
                return;
            }
            ready.TrySetResult(true);
            Log.LogInformation("Watching macOS system sleep and wake notifications");
            NativeMethods.CFRunLoopRun();
        }
        catch (Exception ex)
        {
            ready.TrySetResult(false);
            Log.LogWarning(ex, "macOS system sleep monitor stopped unexpectedly");
        }
        finally
        {
            Interlocked.Exchange(ref _runLoop, nint.Zero);
            if (notifier != 0)
            {
                var result = NativeMethods.IODeregisterForSystemPower(ref notifier);
                if (result != 0 && Log.IsEnabled(LogLevel.Debug)) Log.LogDebug("IODeregisterForSystemPower returned {Result}", result);
            }
            if (notificationPort != nint.Zero)
                NativeMethods.IONotificationPortDestroy(notificationPort);
            var kernelPort = Interlocked.Exchange(ref _kernelPort, 0);
            if (kernelPort != 0)
            {
                var result = NativeMethods.IOServiceClose(kernelPort);
                if (result != 0 && Log.IsEnabled(LogLevel.Debug)) Log.LogDebug("IOServiceClose(system power) returned {Result}", result);
            }
        }
    }

    private void OnPowerMessage(nint _, uint __, uint messageType, nint messageArgument)
    {
        try
        {
            if (messageType == NativeMethods.KIOMessageCanSystemSleep)
            {
                AllowPowerChange(messageArgument);
                return;
            }

            if (messageType == NativeMethods.KIOMessageSystemWillSleep)
            {
                try
                {
                    Coordinator.PrepareForSleepBlocking();
                }
                finally
                {
                    // kIOMessageSystemWillSleep is non-abortable and must always be acknowledged.
                    AllowPowerChange(messageArgument);
                }
                return;
            }

            if (messageType == NativeMethods.KIOMessageSystemWillPowerOn)
            {
                Coordinator.BeginResumeAfterSleep();
                return;
            }

            if (messageType == NativeMethods.KIOMessageSystemHasPoweredOn)
                Coordinator.ResumeAfterSleep();
        }
        catch (Exception ex)
        {
            // Native callbacks must never observe managed exceptions. SystemWillSleep acknowledgement
            // is attempted in its inner finally before control reaches this guard.
            Log.LogWarning(ex, "Failed to handle macOS system power notification");
        }
    }

    private void AllowPowerChange(nint notificationId)
    {
        var kernelPort = Volatile.Read(ref _kernelPort);
        if (kernelPort == 0) return;
        var result = NativeMethods.IOAllowPowerChange(kernelPort, notificationId);
        if (result != 0) Log.LogWarning("IOAllowPowerChange failed ({Result})", result);
    }

    protected override void SignalStop()
    {
        var runLoop = Interlocked.Exchange(ref _runLoop, nint.Zero);
        if (runLoop != nint.Zero) NativeMethods.CFRunLoopStop(runLoop);
    }
}
