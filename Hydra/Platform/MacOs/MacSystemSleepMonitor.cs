using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.MacOs;

[SupportedOSPlatform("macos")]
internal sealed class MacSystemSleepMonitor : IHostedService, IDisposable
{
    private static readonly TimeSpan RelayCloseTimeout = TimeSpan.FromSeconds(5);
    private static readonly nint CoreFoundation =
        NativeLibrary.Load("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation");

    private readonly SystemSleepCoordinator _coordinator;
    private readonly ILogger<MacSystemSleepMonitor> _log;
    private readonly NativeMethods.IOServiceInterestCallback _callback;
    private Thread? _thread;
    private nint _runLoop;
    private uint _kernelPort;

    public MacSystemSleepMonitor(SystemSleepCoordinator coordinator, ILogger<MacSystemSleepMonitor> log)
    {
        _coordinator = coordinator;
        _log = log;
        _callback = OnPowerMessage;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_coordinator.Enabled) return;

        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() => RunNotificationLoop(ready))
        {
            IsBackground = true,
            Name = "HydraSystemSleep"
        };
        _thread.Start();

        if (!await ready.Task.WaitAsync(cancellationToken))
            _log.LogWarning("IOKit system sleep notifications are unavailable; relay sleep suspension is disabled");
    }

    private void RunNotificationLoop(TaskCompletionSource<bool> ready)
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
            _runLoop = runLoop;
            NativeMethods.CFRunLoopAddSource(runLoop, source, GetCfRunLoopCommonModes());
            ready.TrySetResult(true);
            _log.LogInformation("Watching macOS system sleep and wake notifications");
            NativeMethods.CFRunLoopRun();
        }
        catch (Exception ex)
        {
            ready.TrySetResult(false);
            _log.LogWarning(ex, "macOS system sleep monitor stopped unexpectedly");
        }
        finally
        {
            Interlocked.Exchange(ref _runLoop, nint.Zero);
            if (notifier != 0)
            {
                var result = NativeMethods.IODeregisterForSystemPower(ref notifier);
                if (result != 0) _log.LogDebug("IODeregisterForSystemPower returned {Result}", result);
            }
            if (notificationPort != nint.Zero)
                NativeMethods.IONotificationPortDestroy(notificationPort);
            var kernelPort = Interlocked.Exchange(ref _kernelPort, 0);
            if (kernelPort != 0)
            {
                var result = NativeMethods.IOServiceClose(kernelPort);
                if (result != 0) _log.LogDebug("IOServiceClose(system power) returned {Result}", result);
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
                    using var timeout = new CancellationTokenSource(RelayCloseTimeout);
                    _coordinator.PrepareForSleepAsync(timeout.Token).GetAwaiter().GetResult();
                }
                finally
                {
                    // kIOMessageSystemWillSleep is non-abortable and must always be acknowledged.
                    AllowPowerChange(messageArgument);
                }
                return;
            }

            if (messageType == NativeMethods.KIOMessageSystemHasPoweredOn)
                _coordinator.ResumeAfterSleep();
        }
        catch (Exception ex)
        {
            // Native callbacks must never observe managed exceptions. SystemWillSleep acknowledgement
            // is attempted in its inner finally before control reaches this guard.
            _log.LogWarning(ex, "Failed to handle macOS system power notification");
        }
    }

    private void AllowPowerChange(nint notificationId)
    {
        var kernelPort = Volatile.Read(ref _kernelPort);
        if (kernelPort == 0) return;
        var result = NativeMethods.IOAllowPowerChange(kernelPort, notificationId);
        if (result != 0) _log.LogWarning("IOAllowPowerChange failed ({Result})", result);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        var runLoop = Interlocked.Exchange(ref _runLoop, nint.Zero);
        if (runLoop != nint.Zero) NativeMethods.CFRunLoopStop(runLoop);
        var thread = _thread;
        _thread = null;
        if (thread?.Join(RelayCloseTimeout + TimeSpan.FromSeconds(2)) == false)
            _log.LogWarning("macOS system sleep monitor did not stop before its shutdown deadline");
        return Task.CompletedTask;
    }

    public void Dispose() => StopAsync(CancellationToken.None).GetAwaiter().GetResult();

    private static nint GetCfRunLoopCommonModes() =>
        Marshal.ReadIntPtr(NativeLibrary.GetExport(CoreFoundation, "kCFRunLoopCommonModes"));
}
