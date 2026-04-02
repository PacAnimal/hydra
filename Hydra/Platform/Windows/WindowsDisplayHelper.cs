using System.Runtime.InteropServices;

namespace Hydra.Platform.Windows;

internal static class WindowsDisplayHelper
{
    internal static List<DetectedScreen> GetAllScreens()
    {
        var result = new List<DetectedScreen>();

        NativeMethods.EnumDisplayMonitors(nint.Zero, nint.Zero, (nint hMonitor, nint _, ref WINRECT _, nint _) =>
        {
            var info = new MONITORINFOEX { Size = (uint)Marshal.SizeOf<MONITORINFOEX>() };
            if (!NativeMethods.GetMonitorInfoW(hMonitor, ref info)) return true;

            var r = info.Monitor;
            result.Add(new DetectedScreen(
                X: r.Left, Y: r.Top,
                Width: r.Right - r.Left, Height: r.Bottom - r.Top,
                DisplayName: null,
                OutputName: info.DeviceName?.TrimEnd('\0'),
                PlatformId: hMonitor.ToString()));
            return true;
        }, nint.Zero);

        if (result.Count == 0)
        {
            // fallback: primary screen only
            var w = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
            var h = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
            result.Add(new DetectedScreen(0, 0, w, h, null, null, null));
        }

        return result;
    }
}
