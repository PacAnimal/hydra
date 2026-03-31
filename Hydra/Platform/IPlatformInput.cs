using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Screen;

namespace Hydra.Platform;

public interface IPlatformInput : IDisposable
{
    ScreenRect GetPrimaryScreenBounds();
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
