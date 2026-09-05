using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Hydra.Platform.Windows;

[SupportedOSPlatform("windows")]
internal sealed class WindowsSystemSleepMonitor(
    SystemSleepCoordinator coordinator,
    ILogger<WindowsSystemSleepMonitor> log) : IHostedService, IDisposable
{
    private static readonly TimeSpan RelayCloseTimeout = TimeSpan.FromSeconds(5);
    private readonly Lock _callbackLock = new();
    private readonly ManualResetEventSlim _callbacksDrained = new(initialState: true);
    private bool _subscribed;
    private bool _stopping;
    private int _activeCallbacks;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!coordinator.Enabled) return Task.CompletedTask;
        try
        {
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            _subscribed = true;
            log.LogInformation("Watching Windows system suspend and resume notifications");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Windows power notifications are unavailable; relay sleep suspension is disabled");
        }
        return Task.CompletedTask;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs args)
    {
        lock (_callbackLock)
        {
            if (_stopping) return;
            if (_activeCallbacks++ == 0) _callbacksDrained.Reset();
        }
        try
        {
            if (args.Mode == PowerModes.Suspend)
            {
                using var timeout = new CancellationTokenSource(RelayCloseTimeout);
                coordinator.PrepareForSleepAsync(timeout.Token).GetAwaiter().GetResult();
            }
            else if (args.Mode == PowerModes.Resume)
            {
                coordinator.ResumeAfterSleep();
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to handle Windows system power notification");
        }
        finally
        {
            lock (_callbackLock)
                if (--_activeCallbacks == 0)
                    _callbacksDrained.Set();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_callbackLock)
        {
            _stopping = true;
            if (_subscribed)
            {
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                _subscribed = false;
            }
        }
        _callbacksDrained.Wait(RelayCloseTimeout + TimeSpan.FromSeconds(1), cancellationToken);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        _callbacksDrained.Dispose();
    }
}
