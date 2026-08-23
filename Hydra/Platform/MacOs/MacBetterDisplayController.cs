using System.Diagnostics;

namespace Hydra.Platform.MacOs;

// BetterDisplay owns its display state, combined brightness range, and OSD. When it is running,
// use its documented DistributedNotificationCenter integration instead of bypassing it with DDC.
internal sealed class MacBetterDisplayController
{
    private const string ProcessName = "BetterDisplay";
    private const string RequestName = "pro.betterdisplay.BetterDisplay.request";
    private static readonly nint Center = NativeMethods.CFNotificationCenterGetDistributedCenter();

    internal bool TryAdjustMainDisplayBrightness(bool increase)
    {
        if (Center == nint.Zero || !IsRunning()) return false;

        // BetterDisplay interprets percentage values as relative offsets, and with only the mini's
        // main display selected it chooses its configured combined/hardware/software route itself.
        var offset = increase ? "+5%" : "-5%";
        var request = NativeMethods.MakeNsString(
            "{\"commands\":[\"set\"],\"parameters\":{\"brightness\":\"" + offset
            + "\",\"offset\":null,\"displayWithMainStatus\":null}}");
        var name = NativeMethods.MakeNsString(RequestName);
        try
        {
            NativeMethods.CFNotificationCenterPostNotification(Center, name, request, nint.Zero, 1);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            NativeMethods.CFRelease(name);
            NativeMethods.CFRelease(request);
        }
    }

    private static bool IsRunning()
    {
        try { return Process.GetProcessesByName(ProcessName).Length > 0; }
        catch { return false; }
    }
}
