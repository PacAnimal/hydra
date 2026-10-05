using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform;

// A sleep monitor whose native notifications arrive on a message loop it runs on a thread of its own.
internal abstract class ThreadedSleepMonitor(SystemSleepCoordinator coordinator, ILogger log) : IHostedService, IDisposable
{
    private Thread? _thread;
    private volatile bool _stopping;

    protected SystemSleepCoordinator Coordinator => coordinator;
    protected ILogger Log => log;
    protected bool Stopping => _stopping;

    // e.g. "IOKit system sleep notifications"
    protected abstract string NotificationSource { get; }

    // e.g. "macOS system sleep monitor"
    protected abstract string MonitorName { get; }

    // runs until Stopping, reporting through ready whether notifications are being delivered
    protected abstract void RunLoop(TaskCompletionSource<bool> ready);

    // wakes a loop blocked in native code
    protected virtual void SignalStop() { }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!coordinator.Enabled) return;

        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() => RunLoop(ready))
        {
            IsBackground = true,
            Name = "HydraSystemSleep"
        };
        _thread.Start();

        if (!await ready.Task.WaitAsync(cancellationToken))
            log.LogWarning("{Source} are unavailable; relay sleep suspension is disabled", NotificationSource);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        SignalStop();
        var thread = _thread;
        _thread = null;
        if (thread?.Join(SystemSleepCoordinator.RelayCloseTimeout + TimeSpan.FromSeconds(2)) == false)
            log.LogWarning("{Monitor} did not stop before its shutdown deadline", MonitorName);
        return Task.CompletedTask;
    }

    public void Dispose() => StopAsync(CancellationToken.None).GetAwaiter().GetResult();
}
