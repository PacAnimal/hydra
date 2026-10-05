using System.Runtime.Versioning;
using Hydra.Management;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Hydra.Platform.Windows;

/// <summary>
/// Stops the application when the service watchdog signals HydraSessionStop. The session child can start at
/// the logon screen, and a sign-in to its session restarts nothing, so its watcher also re-stamps the private
/// sidecar each time a console user appears.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SessionChildLifetime : IHostedService, IDisposable
{
    internal const string StopEventName = "HydraSessionStop";

    private readonly IHostApplicationLifetime _lifetime;
    private readonly string _stopEventName;
    private readonly Func<CancellationToken, Task> _restamp;
    private readonly ILogger<SessionChildLifetime> _log;
    private readonly ConsoleSignInRestamper _restamper;
    private readonly CancellationTokenSource _stopping = new();
    private SafeFileHandle? _stopEvent;
    private Thread? _thread;
    private bool _lookupFailing;

    public SessionChildLifetime(IHostApplicationLifetime lifetime, RemoteManagementStore store, ILogger<SessionChildLifetime> log)
        : this(lifetime, StopEventName, () => Win32Session.ActiveConsoleUser()?.Value, store.Restamp, log) { }

    internal SessionChildLifetime(IHostApplicationLifetime lifetime, string stopEventName, Func<string?> consoleUser,
        Func<CancellationToken, Task> restamp, ILogger<SessionChildLifetime> log)
    {
        _lifetime = lifetime;
        _stopEventName = stopEventName;
        _restamp = restamp;
        _log = log;
        _restamper = new ConsoleSignInRestamper(consoleUser, StartRestamp);
    }

    public Task StartAsync(CancellationToken cancel)
    {
        _stopEvent = Win32Session.OpenGlobalEvent(_stopEventName);
        _thread = new Thread(Watch) { IsBackground = true, Name = "session-stop-watcher" };
        _thread.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancel)
    {
        // wake the watcher so it exits promptly when the host stops for any reason (not just the
        // watchdog signal) — otherwise it parked on an infinite wait until process death, leaking the thread
        _stopping.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(2));
        return Task.CompletedTask;
    }

    private void Watch()
    {
        CheckConsoleUser();
        if (_stopEvent == null || _stopEvent.IsInvalid) return; // running standalone, not under service
        // poll so StopAsync can break us out; the watchdog signals the event for the real stop
        while (!_stopping.IsCancellationRequested)
        {
            if (Win32Session.WaitForEvent(_stopEvent, 250))
            {
                _lifetime.StopApplication();
                return;
            }
            CheckConsoleUser();
        }
    }

    // the watcher must outlive a failed console-user lookup, or the stop signal goes unheard
    private void CheckConsoleUser()
    {
        try
        {
            _ = _restamper.Check();
            _lookupFailing = false;
        }
        catch (Exception ex)
        {
            // once a streak, as this runs four times a second
            _log.Log(_lookupFailing ? LogLevel.Debug : LogLevel.Warning, ex, "Could not check the console user");
            _lookupFailing = true;
        }
    }

    // off the watcher, so a slow sidecar lock delays neither startup nor the stop signal
    private Task<bool> StartRestamp(bool retry) => Task.Run(() => RestampManagementState(retry, _stopping.Token));

    // an unreadable sidecar only costs an unelevated pair, so it must not stop the session child
    private async Task<bool> RestampManagementState(bool retry, CancellationToken cancel)
    {
        try
        {
            await _restamp(cancel);
            _log.LogInformation("Re-stamped the remote-management state for the console user");
            return true;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            _log.LogDebug("Re-stamp abandoned: the session child is stopping");
            return false;
        }
        catch (Exception ex)
        {
            _log.Log(retry ? LogLevel.Debug : LogLevel.Warning, ex, "Could not re-stamp the remote-management state for this console user");
            return false;
        }
    }

    public void Dispose()
    {
        _stopEvent?.Dispose();
        _stopping.Dispose();
    }
}
