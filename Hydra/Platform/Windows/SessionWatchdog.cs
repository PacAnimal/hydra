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
    private readonly Boxed<ChildProcess?> _child = new(null);
    private SafeFileHandle? _stopEvent;
    private TimeSpan _backoff = TimeSpan.FromSeconds(1);
    private DateTime _childStartedAt = DateTime.MinValue;
    // the child must stay alive at least this long before we trust it and reset the crash backoff —
    // longer than the 30s cap so a child that keeps dying shortly after launch keeps escalating
    private static readonly TimeSpan BackoffResetAfter = TimeSpan.FromSeconds(60);

    protected override async Task Execute(CancellationToken cancel)
    {
        // lazy init so event exists before the child process is launched
        _stopEvent ??= Win32Session.CreateGlobalEvent("HydraSessionStop", manualReset: true);

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
        else if (ClaimIfExited() is { } exited)
        {
            log.LogWarning("Child exited unexpectedly, restarting in {Backoff}s", (int)_backoff.TotalSeconds);
            exited.Dispose();
            await Task.Delay(_backoff, cancel);
            _backoff = TimeSpan.FromSeconds(Math.Min(_backoff.TotalSeconds * 2, 30));
            if (session != Win32Session.NoSession)
                StartChild(session);
        }
        else
        {
            // only reset the backoff once the child has proven stable. Resetting on every alive tick let a
            // child that crashes shortly after each launch restart-storm forever (the 30s cap never engaged).
            if (_childStartedAt != DateTime.MinValue && DateTime.UtcNow - _childStartedAt >= BackoffResetAfter)
                _backoff = TimeSpan.FromSeconds(1);
        }
    }

    protected override async Task OnShutdown(CancellationToken cancel)
    {
        await StopChildAsync();
        _stopEvent?.Dispose();
    }

    private ChildProcess? ClaimIfExited()
    {
        lock (_child)
        {
            if (_child.Value == null || !Win32Session.HasProcessExited(_child.Value.Handle)) return null;
            var child = _child.Value;
            _child.Value = null;
            return child;
        }
    }

    private void StartChild(uint session)
    {
        var exe = Environment.ProcessPath!;
        try
        {
            var child = Win32Session.LaunchInSession(session, exe, "--session");
            lock (_child) { _child.Value = child; }
            _childStartedAt = DateTime.UtcNow;
            log.LogInformation("Child launched in session {Session} (PID {Pid})", session, child.Pid);
        }
        catch (Exception ex)
        {
            _childStartedAt = DateTime.MinValue; // failed launch is not a healthy start
            log.LogError(ex, "failed to launch child in session {Session}", session);
        }
    }

    internal async Task StopChildAsync()
    {
        ChildProcess? child;
        lock (_child) { child = _child.Value; _child.Value = null; }
        _childStartedAt = DateTime.MinValue; // no current child — don't let a stale timestamp reset backoff
        if (child == null) return;

        if (_stopEvent != null) Win32Session.SignalEvent(_stopEvent);

        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline && !Win32Session.HasProcessExited(child.Handle))
                await Task.Delay(100);

            if (!Win32Session.HasProcessExited(child.Handle))
            {
                log.LogWarning("Child did not stop in time, terminating");
                Win32Session.KillProcess(child.Handle);
            }
        }
        finally
        {
            child.Dispose();
            if (_stopEvent != null) Win32Session.ResetGlobalEvent(_stopEvent);
        }
    }
}
