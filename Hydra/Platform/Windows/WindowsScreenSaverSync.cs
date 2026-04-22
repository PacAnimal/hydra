using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Windows;

public sealed class WindowsScreenSaverSync(ILogger<WindowsScreenSaverSync> log) : PollingScreenSaverSync(log)
{
    public override void Activate()
    {
        log.LogInformation("Activating screensaver");
        NativeMethods.PostMessage(NativeMethods.GetDesktopWindow(), NativeMethods.WM_SYSCOMMAND, NativeMethods.SC_SCREENSAVE, nint.Zero);
    }

    public override void Deactivate()
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

    public override void Suppress()
    {
        log.LogDebug("Refreshing screensaver suppression (SetThreadExecutionState)");
        _ = NativeMethods.SetThreadExecutionState(NativeMethods.ES_DISPLAY_REQUIRED);
    }

    public override void Restore() { }

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
