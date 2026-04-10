using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Windows;

/// <summary>Routes SendInput calls to a worker thread always attached to the current input desktop.</summary>
/// <remarks>
/// SendInput is desktop-scoped: calls from a thread attached to winsta0\Default are silently dropped
/// when the active input desktop is winsta0\Winlogon (lock screen) or the secure desktop (UAC prompts).
/// This class polls OpenInputDesktop every 200ms and re-attaches the worker thread via SetThreadDesktop
/// whenever the input desktop changes.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class DesktopInputDispatcher : IDisposable
{
    // DESKTOP_READOBJECTS | DESKTOP_WRITEOBJECTS — minimum rights the interactive user has on all desktops
    // including Winlogon. GENERIC_WRITE would also request journal rights the user doesn't have, causing failure.
    private const uint DesktopAccess = NativeMethods.DESKTOP_READOBJECTS | NativeMethods.DESKTOP_WRITEOBJECTS;

    private readonly ILogger _log;
    private readonly BlockingCollection<Action> _queue = [];
    private readonly System.Threading.Timer _pollTimer;
    private nint _activeDesktop;
    private string _activeDesktopName = "";
    private bool _disposed;

    internal DesktopInputDispatcher(ILogger log)
    {
        _log = log;
        _activeDesktop = NativeMethods.OpenInputDesktop(NativeMethods.DF_ALLOWOTHERACCOUNTHOOK, true, DesktopAccess);
        _activeDesktopName = GetDesktopName(_activeDesktop);
        if (_activeDesktop == nint.Zero)
            _log.LogWarning("OpenInputDesktop failed at startup (error {Error}) — input may not work on secure desktops", Marshal.GetLastWin32Error());
        else
            _log.LogInformation("Desktop input dispatcher started, current desktop: {Name}", _activeDesktopName);
        StartWorker(_activeDesktop);
        _pollTimer = new System.Threading.Timer(_ => PollDesktop(), null, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200));
    }

    internal void Dispatch(Action action)
    {
        if (!_disposed)
            _queue.TryAdd(action);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer.Dispose();
        _queue.CompleteAdding();
        if (_activeDesktop != nint.Zero)
        {
            NativeMethods.CloseDesktop(_activeDesktop);
            _activeDesktop = nint.Zero;
        }
    }

    private void StartWorker(nint hDesk)
    {
        var t = new Thread(() =>
        {
            if (hDesk != nint.Zero)
            {
                if (!NativeMethods.SetThreadDesktop(hDesk))
                    _log.LogWarning("SetThreadDesktop failed at worker startup (error {Error})", Marshal.GetLastWin32Error());
            }
            foreach (var action in _queue.GetConsumingEnumerable())
                action();
        })
        {
            IsBackground = true,
            Name = "HydraDesktopInput",
        };
        t.Start();
    }

    private void PollDesktop()
    {
        if (_disposed) return;

        var hDesk = NativeMethods.OpenInputDesktop(NativeMethods.DF_ALLOWOTHERACCOUNTHOOK, true, DesktopAccess);
        if (hDesk == nint.Zero)
        {
            _log.LogDebug("OpenInputDesktop returned null during poll (error {Error})", Marshal.GetLastWin32Error());
            return;
        }

        var name = GetDesktopName(hDesk);
        if (name == _activeDesktopName)
        {
            NativeMethods.CloseDesktop(hDesk);
            return;
        }

        _log.LogInformation("Input desktop changed: {Old} → {New}", _activeDesktopName, name);

        var oldDesk = _activeDesktop;
        _activeDesktop = hDesk;
        _activeDesktopName = name;

        // re-attach the worker thread to the new desktop (it has no windows or hooks, so SetThreadDesktop succeeds)
        // close the old handle after switching so it remains valid until the thread detaches
        _queue.TryAdd(() =>
        {
            if (!NativeMethods.SetThreadDesktop(hDesk))
                _log.LogWarning("SetThreadDesktop failed for desktop {Name} (error {Error})", name, Marshal.GetLastWin32Error());
            if (oldDesk != nint.Zero)
                NativeMethods.CloseDesktop(oldDesk);
        });
    }

    private static unsafe string GetDesktopName(nint hDesk)
    {
        if (hDesk == nint.Zero) return "";
        const int bufSize = 128;
        char* buf = stackalloc char[bufSize];
        return NativeMethods.GetUserObjectInformationW(hDesk, NativeMethods.UOI_NAME, (nint)buf, (uint)(bufSize * sizeof(char)), out _)
            ? new string(buf)
            : "";
    }
}
