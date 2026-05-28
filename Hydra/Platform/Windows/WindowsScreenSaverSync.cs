using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsScreenSaverSync(ILogger<WindowsScreenSaverSync> log) : PollingScreenSaverSync(log)
{
    private readonly ILogger<WindowsScreenSaverSync> _log = log;
    private StaMessageLoop? _lockLoop;
    private WndProc? _lockWndProc;  // keep-alive to prevent GC
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
        _lockWndProc = null;
        return Task.CompletedTask;
    }

    private void StartLockWatcher()
    {
        var hwnd = nint.Zero;
        var className = Marshal.StringToHGlobalUni("HydraLockWatcher");

        _lockWndProc = (h, msg, wParam, lParam) =>
        {
            if (msg == NativeMethods.WM_WTSSESSION_CHANGE && wParam == NativeMethods.WTS_SESSION_LOCK)
                OnScreenLocked();
            else if (msg == NativeMethods.WM_WTSSESSION_CHANGE && wParam == NativeMethods.WTS_SESSION_UNLOCK)
                OnScreenUnlocked();
            return NativeMethods.DefWindowProcW(h, msg, wParam, lParam);
        };

        try
        {
            _lockLoop = new StaMessageLoop("lock-watcher",
                init: () =>
                {
                    var hInst = NativeMethods.GetModuleHandleW(nint.Zero);
                    var wc = new NativeMethods.WNDCLASSEXW
                    {
                        cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
                        lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_lockWndProc),
                        hInstance = hInst,
                        lpszClassName = className,
                    };
                    var atom = NativeMethods.RegisterClassExW(in wc);
                    if (atom == 0)
                    {
                        _log.LogWarning("RegisterClassExW failed for lock watcher (error {Error})", Marshal.GetLastWin32Error());
                        return;
                    }
                    hwnd = NativeMethods.CreateWindowExW(0, atom, nint.Zero, 0,
                        0, 0, 0, 0, NativeMethods.HWND_MESSAGE, nint.Zero, hInst, nint.Zero);
                    if (hwnd == nint.Zero)
                    {
                        _log.LogWarning("CreateWindowExW failed for lock watcher (error {Error})", Marshal.GetLastWin32Error());
                        return;
                    }
                    if (!NativeMethods.WTSRegisterSessionNotification(hwnd, NativeMethods.NOTIFY_FOR_THIS_SESSION))
                        _log.LogWarning("WTSRegisterSessionNotification failed (error {Error})", Marshal.GetLastWin32Error());
                    else
                        _log.LogInformation("Watching for session lock notifications");
                },
                onExit: () =>
                {
                    if (hwnd != nint.Zero)
                    {
                        NativeMethods.WTSUnRegisterSessionNotification(hwnd);
                        NativeMethods.DestroyWindow(hwnd);
                    }
                    Marshal.FreeHGlobal(className);
                });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to start lock watcher");
            Marshal.FreeHGlobal(className);
        }
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

    public override void ResetIdleTimer() => _ = NativeMethods.SetThreadExecutionState(NativeMethods.ES_DISPLAY_REQUIRED);

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
