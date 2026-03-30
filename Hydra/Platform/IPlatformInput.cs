using Hydra.Screen;

namespace Hydra.Platform;

public interface IPlatformInput : IDisposable
{
    ScreenRect GetPrimaryScreenBounds();
    void WarpCursor(int x, int y);
    void HideCursor();
    void ShowCursor();
    void StartEventTap(Action<double, double, long, long> onMouseMove);
    void StopEventTap();
    bool IsAccessibilityTrusted();
}
