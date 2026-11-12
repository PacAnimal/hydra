using Hydra.Config;
using Hydra.Screen;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Linux;

// Monitors as Hyprland lays them out, in the logical coordinates its cursor position and movecursor use.
// XWayland's view of the same outputs can differ (scaling, rotation), so a Wayland session reads the compositor.
public sealed class HyprlandScreenDetector(IHydraProfile profile, ILogger<HyprlandScreenDetector> log) : ScreenDetector(profile, log)
{
    private List<DetectedScreen> _last = [];

    protected override List<DetectedScreen> Detect()
    {
        try
        {
            _last = HyprlandIpc.Monitors()
                .Select(m => new DetectedScreen(m.X, m.Y, m.Width, m.Height, m.Description, m.Name, m.Name))
                .ToList();
        }
        catch (Exception e)
        {
            // a failed query keeps the last layout: an empty one would drop every local screen
            if (log.IsEnabled(LogLevel.Debug)) log.LogDebug(e, "Hyprland monitor query failed");
        }
        return _last;
    }
}

// focuses the monitor holding the screen's centre; Hyprland then focuses that monitor's last window itself
internal sealed class HyprlandWindowFocus : IWindowFocus
{
    public void FocusFrontmostOn(ScreenBounds screen)
    {
        var cx = screen.X + screen.Width / 2;
        var cy = screen.Y + screen.Height / 2;
        try
        {
            var monitor = HyprlandIpc.Monitors().FirstOrDefault(m => cx >= m.X && cx < m.X + m.Width && cy >= m.Y && cy < m.Y + m.Height);
            if (monitor != null) HyprlandIpc.FocusMonitor(monitor.Name);
        }
        catch
        {
            // compositor unreachable: focus stays where it was
        }
    }
}
