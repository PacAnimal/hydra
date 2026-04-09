using System.Runtime.Versioning;
using Cathedral.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Hydra.Platform.Windows;

[SupportedOSPlatform("windows")]
internal sealed class SessionWatchdog(ILogger<SessionWatchdog> log)
    : SimpleHostedService(log, TimeSpan.FromMilliseconds(500))
{
    private uint _lastSession = Win32Session.NoSession;
    private ChildProcess? _child;
    private SafeFileHandle? _stopEvent;
    private SafeFileHandle? _updateEvent;
    private TimeSpan _backoff = TimeSpan.FromSeconds(1);

    protected override async Task Execute(CancellationToken cancel)
    {
        // lazy init so events exist before the child process is launched
        _stopEvent ??= Win32Session.CreateGlobalEvent("HydraSessionStop", manualReset: true);
        _updateEvent ??= Win32Session.CreateGlobalEvent("HydraUpdateReady", manualReset: false);

        var session = Win32Session.GetActiveConsoleSessionId();

        if (session != _lastSession)
        {
            if (_lastSession != Win32Session.NoSession)
            {
                log.LogInformation("Session changed {Old} → {New}, restarting child", _lastSession, session);
                await StopChildAsync();
            }
            _lastSession = session;
            if (session != Win32Session.NoSession)
                StartChild(session);
        }
        else if (_child != null && Win32Session.HasProcessExited(_child.Handle))
        {
            log.LogWarning("Child exited unexpectedly, restarting in {Backoff}s", (int)_backoff.TotalSeconds);
            _child.Dispose();
            _child = null;
            await Task.Delay(_backoff, cancel);
            _backoff = TimeSpan.FromSeconds(Math.Min(_backoff.TotalSeconds * 2, 30));
            if (session != Win32Session.NoSession)
                StartChild(session);
        }
        else
        {
            _backoff = TimeSpan.FromSeconds(1);
        }

        // non-blocking check for staged update signal
        if (_updateEvent != null && Win32Session.WaitForEvent(_updateEvent, 0))
            HandleUpdate();
    }

    protected override async Task OnShutdown(CancellationToken cancel)
    {
        await StopChildAsync();
        _stopEvent?.Dispose();
        _updateEvent?.Dispose();
    }

    private void StartChild(uint session)
    {
        var exe = Environment.ProcessPath!;
        try
        {
            _child = Win32Session.LaunchInSession(session, exe, "--session");
            log.LogInformation("Child launched in session {Session} (PID {Pid})", session, _child.Pid);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "failed to launch child in session {Session}", session);
        }
    }

    private async Task StopChildAsync()
    {
        if (_child == null) return;

        if (_stopEvent != null) Win32Session.SignalEvent(_stopEvent);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && !Win32Session.HasProcessExited(_child.Handle))
            await Task.Delay(100);

        if (!Win32Session.HasProcessExited(_child.Handle))
        {
            log.LogWarning("Child did not stop in time, terminating");
            Win32Session.KillProcess(_child.Handle);
        }

        _child.Dispose();
        _child = null;

        if (_stopEvent != null) Win32Session.ResetGlobalEvent(_stopEvent);
    }

    private void HandleUpdate()
    {
        log.LogInformation("Update applied by child, restarting service");
        // exit non-zero so SCM's failure action (restart/5000) fires and picks up the new binary
        Environment.Exit(1);
    }
}
