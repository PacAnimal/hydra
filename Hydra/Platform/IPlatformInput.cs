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
    public ScreenBounds Bounds => new(X, Y, Width, Height);
}

public record KeyRepeatSettings(int DelayMs, int RateMs);

// event tap interface — implemented by platform input handlers; used by slaves to passively observe local input for activity tracking
public interface ILocalEventTap
{
    bool IsAccessibilityTrusted() => true;
    Task WaitForAccessibilityTrusted(CancellationToken cancel) => Task.CompletedTask;
    Task StartEventTap(
        Action<double, double> onMouseMove,
        Action<double, double>? onMouseDelta,
        Action<KeyEvent> onKeyEvent,
        Action<MouseButtonEvent> onMouseButton,
        Action<MouseScrollEvent> onMouseScroll,
        Action? onLocalActivity = null);
    void StopEventTap();
    Task RestartEventTap() => Task.CompletedTask;
}

public interface IPlatformInput : IAsyncDisposable, ICursor, ILocalEventTap
{
    bool IsOnVirtualScreen { get; set; }

    KeyRepeatSettings GetKeyRepeatSettings();
    bool AnyMouseButtonHeld();
}
