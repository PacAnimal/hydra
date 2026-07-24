using System.Runtime.Versioning;
using Cathedral.Utils;
using Microsoft.Extensions.Hosting;
using Microsoft.Win32.SafeHandles;

namespace Hydra.Platform.Windows;

/// <summary>Stops the application when the service watchdog signals HydraSessionStop.</summary>
[SupportedOSPlatform("windows")]
internal sealed class SessionChildLifetime(IHostApplicationLifetime lifetime) : IHostedService, IDisposable
{
    private SafeFileHandle? _stopEvent;
    private Thread? _thread;
    private readonly Toggle _stopping = new();

    public Task StartAsync(CancellationToken cancel)
    {
        _stopEvent = Win32Session.OpenGlobalEvent("HydraSessionStop");
        if (_stopEvent == null) return Task.CompletedTask; // running standalone, not under service

        _thread = new Thread(WaitForStop) { IsBackground = true, Name = "session-stop-watcher" };
        _thread.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancel)
    {
        // wake the watcher so it exits promptly when the host stops for any reason (not just the
        // watchdog signal) — otherwise it parked on an infinite wait until process death, leaking the thread
        _stopping.TrySet();
        _thread?.Join(TimeSpan.FromSeconds(2));
        return Task.CompletedTask;
    }

    private void WaitForStop()
    {
        if (_stopEvent == null || _stopEvent.IsInvalid) return;
        // poll so StopAsync (_stopping) can break us out; the watchdog signals the event for the real stop
        while (!_stopping)
        {
            if (Win32Session.WaitForEvent(_stopEvent, 250))
            {
                lifetime.StopApplication();
                return;
            }
        }
    }

    public void Dispose() => _stopEvent?.Dispose();
}
