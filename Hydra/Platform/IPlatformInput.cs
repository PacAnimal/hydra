using Hydra.Keyboard;
using Hydra.Screen;

namespace Hydra.Platform;

public interface IPlatformInput : IDisposable
{
    ScreenRect GetPrimaryScreenBounds();
    void WarpCursor(int x, int y);
    void HideCursor();
    void ShowCursor();
    void StartEventTap(Action<double, double> onMouseMove, Action<KeyEvent> onKeyEvent);
    void StopEventTap();
    bool IsAccessibilityTrusted();
    bool IsOnVirtualScreen { get; set; }
}
