using System.Diagnostics;

namespace Hydra.Platform.MacOs;

// BetterDisplay owns its display state, combined brightness range, and OSD. When it is running,
// use its documented DistributedNotificationCenter integration instead of bypassing it with DDC.
internal static class MacBetterDisplayController
{
    private const string ProcessName = "BetterDisplay";
    private const string RequestName = "pro.betterdisplay.BetterDisplay.request";
    private static readonly nint Center = NativeMethods.CFNotificationCenterGetDistributedCenter();

    internal static bool TryAdjustMainDisplayBrightness(bool increase)
    {
        if (Center == nint.Zero || !IsRunning()) return false;

        // BetterDisplay interprets percentage values as relative offsets, and with only the mini's
        // main display selected it chooses its configured combined/hardware/software route itself.
        var offset = increase ? "+5%" : "-5%";
        using var request = new NsString(
            "{\"commands\":[\"set\"],\"parameters\":{\"brightness\":\"" + offset
            + "\",\"offset\":null,\"displayWithMainStatus\":null}}");
        using var name = new NsString(RequestName);
        try
        {
            NativeMethods.CFNotificationCenterPostNotification(Center, name, request, nint.Zero, 1);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsRunning()
    {
        try { return Process.GetProcessesByName(ProcessName).Length > 0; }
        catch { return false; }
    }
}
