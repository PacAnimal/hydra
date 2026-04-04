using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Screen;

namespace Hydra.Platform;

// platform-detected screen with all available identifiers for config matching
public record DetectedScreen(
    int X, int Y, int Width, int Height,
    string? DisplayName,   // e.g. "DELL U2720Q", "Built-in Retina Display"
    string? OutputName,    // e.g. "HDMI-1", "eDP-1", "\\.\DISPLAY1"
    string? PlatformId)    // platform-specific ID: CGDirectDisplayID, HMONITOR, XRandR output id
    : IBounded
{
    public (int X, int Y, int Width, int Height) Bounds => (X, Y, Width, Height);
}

public interface IPlatformInput : IDisposable
{
    List<DetectedScreen> GetAllScreens();
    void WarpCursor(int x, int y);
    void HideCursor();
    void ShowCursor();
    void StartEventTap(
        Action<double, double> onMouseMove,
        Action<KeyEvent> onKeyEvent,
        Action<MouseButtonEvent> onMouseButton,
        Action<MouseScrollEvent> onMouseScroll);
    void StopEventTap();
    bool IsAccessibilityTrusted();
    bool IsOnVirtualScreen { get; set; }
}
