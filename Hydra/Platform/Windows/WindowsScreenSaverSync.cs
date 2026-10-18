using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsScreenSaverSync(ILogger<WindowsScreenSaverSync> log) : PollingScreenSaverSync(log)
{
    private readonly ILogger<WindowsScreenSaverSync> _log = log;
    private StaMessageLoop? _lockLoop;
    private bool _lockLoopStarted;

    protected override Task Execute(CancellationToken cancel)
    {
        if (!_lockLoopStarted)
        {
            _lockLoopStarted = true;
            StartLockWatcher();
        }
        return base.Execute(cancel);
    }

    protected override Task OnShutdown(CancellationToken cancel)
    {
        var loop = Interlocked.Exchange(ref _lockLoop, null);
        loop?.Dispose();
        return Task.CompletedTask;
    }

    private void StartLockWatcher()
    {
        Win32Window? window = null;
        try
        {
            _lockLoop = new StaMessageLoop("lock-watcher",
                init: () =>
                {
                    window = Win32Window.MessageOnly("HydraLockWatcher", LockWndProc);
                    if (!NativeMethods.WTSRegisterSessionNotification(window.Handle, NativeMethods.NOTIFY_FOR_THIS_SESSION))
                        _log.LogWarning("WTSRegisterSessionNotification failed (error {Error})", Marshal.GetLastWin32Error());
                    else
                        _log.LogInformation("Watching for session lock notifications");
                },
                onExit: () =>
                {
                    if (window == null) return;
                    NativeMethods.WTSUnRegisterSessionNotification(window.Handle);
                    window.Dispose();
                });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to start lock watcher");
        }
    }

    private nint LockWndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == NativeMethods.WM_WTSSESSION_CHANGE && wParam == NativeMethods.WTS_SESSION_LOCK)
            OnScreenLocked();
        else if (msg == NativeMethods.WM_WTSSESSION_CHANGE && wParam == NativeMethods.WTS_SESSION_UNLOCK)
            OnScreenUnlocked();
        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public override void LockScreen()
    {
        _log.LogInformation("Locking screen (LockWorkStation)");
        NativeMethods.LockWorkStation();
    }

    public override void Activate()
    {
        _log.LogInformation("Activating screensaver");
        NativeMethods.PostMessage(NativeMethods.GetDesktopWindow(), NativeMethods.WM_SYSCOMMAND, NativeMethods.SC_SCREENSAVE, nint.Zero);
    }

    public override void Deactivate()
    {
        _log.LogInformation("Deactivating screensaver");
        // close the foreground window (screensaver) then reset the idle timer
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd != nint.Zero)
            NativeMethods.PostMessage(hwnd, NativeMethods.WM_CLOSE, nint.Zero, nint.Zero);

        // toggle SPI_SETSCREENSAVEACTIVE off/on to reset the idle timer
        NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETSCREENSAVEACTIVE, 0, nint.Zero, 0);
        NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETSCREENSAVEACTIVE, 1, nint.Zero, 0);
    }

    public override void ResetIdleTimer()
    {
        // SetThreadExecutionState doesn't update GetLastInputInfo() which the screensaver polls; SendInput does.
        // use a keyboard event (VK_NONAME key-up) rather than a mouse move so apps that hide the cursor
        // during typing (e.g. WezTerm) don't see it flash back.
        _ = InputSender.Key(NativeMethods.VK_NONAME, NativeMethods.KEYEVENTF_KEYUP);
    }

    protected override bool IsScreensaverOn()
    {
        var ptr = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(ptr, 0);
            NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETSCREENSAVERRUNNING, 0, ptr, 0);
            return Marshal.ReadInt32(ptr) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
