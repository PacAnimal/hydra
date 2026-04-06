using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Windows;

public sealed class WindowsScreenSaverSync(ILogger<WindowsScreenSaverSync> log) : IScreenSaverSync
{
    private CancellationTokenSource? _watchCts;

    public void StartWatching(Action onActivated, Action onDeactivated)
    {
        log.LogInformation("Watching for screensaver state changes (polling)");
        _watchCts = new CancellationTokenSource();
        var ct = _watchCts.Token;
        _ = Task.Run(async () => await PollAsync(onActivated, onDeactivated, ct), ct);
    }

    public void StopWatching()
    {
        log.LogInformation("Stopped watching for screensaver state changes");
        _watchCts?.Cancel();
        _watchCts?.Dispose();
        _watchCts = null;
    }

    public void Activate()
    {
        log.LogInformation("Activating screensaver");
        NativeMethods.PostMessage(NativeMethods.GetDesktopWindow(), NativeMethods.WM_SYSCOMMAND, NativeMethods.SC_SCREENSAVE, nint.Zero);
    }

    public void Deactivate()
    {
        log.LogInformation("Deactivating screensaver");
        // close the foreground window (screensaver) then reset the idle timer
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd != nint.Zero)
            NativeMethods.PostMessage(hwnd, NativeMethods.WM_CLOSE, nint.Zero, nint.Zero);

        // toggle SPI_SETSCREENSAVEACTIVE off/on to reset the idle timer
        NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETSCREENSAVEACTIVE, 0, nint.Zero, 0);
        NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETSCREENSAVEACTIVE, 1, nint.Zero, 0);
    }

    public void Suppress()
    {
        log.LogDebug("Refreshing screensaver suppression (SetThreadExecutionState)");
        _ = NativeMethods.SetThreadExecutionState(NativeMethods.ES_DISPLAY_REQUIRED);
    }

    public void Restore() { }

    private async Task PollAsync(Action onActivated, Action onDeactivated, CancellationToken ct)
    {
        var wasRunning = false;
        var ptr = Marshal.AllocHGlobal(4);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                Marshal.WriteInt32(ptr, 0);
                NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETSCREENSAVERRUNNING, 0, ptr, 0);
                var isRunning = Marshal.ReadInt32(ptr) != 0;
                if (isRunning && !wasRunning)
                {
                    log.LogInformation("Screensaver started (poll detected)");
                    onActivated();
                }
                else if (!isRunning && wasRunning)
                {
                    log.LogInformation("Screensaver stopped (poll detected)");
                    onDeactivated();
                }
                wasRunning = isRunning;
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
