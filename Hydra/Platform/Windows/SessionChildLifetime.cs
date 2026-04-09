using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting;
using Microsoft.Win32.SafeHandles;

namespace Hydra.Platform.Windows;

/// <summary>Stops the application when the service watchdog signals HydraSessionStop.</summary>
[SupportedOSPlatform("windows")]
internal sealed class SessionChildLifetime(IHostApplicationLifetime lifetime) : IHostedService
{
    private SafeFileHandle? _stopEvent;
    private Thread? _thread;

    public Task StartAsync(CancellationToken cancel)
    {
        _stopEvent = Win32Session.OpenGlobalEvent("HydraSessionStop");
        if (_stopEvent == null) return Task.CompletedTask; // running standalone, not under service

        _thread = new Thread(WaitForStop) { IsBackground = true, Name = "session-stop-watcher" };
        _thread.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancel) => Task.CompletedTask;

    private void WaitForStop()
    {
        if (_stopEvent == null || _stopEvent.IsInvalid) return;
        Win32Session.WaitForEvent(_stopEvent, Win32Session.Infinite);
        lifetime.StopApplication();
    }
}
