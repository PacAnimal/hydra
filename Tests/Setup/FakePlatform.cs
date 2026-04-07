using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Platform;

namespace Tests.Setup;

public sealed class FakePlatform : IPlatformInput
{
    private Action<double, double>? _onMouseMove;
    private Action<KeyEvent>? _onKeyEvent;
    private Action<MouseButtonEvent>? _onMouseButton;
    private Action<MouseScrollEvent>? _onMouseScroll;

    public bool IsOnVirtualScreen { get; set; }
    public bool HideCursorCalled { get; set; }
    public bool ShowCursorCalled { get; set; }
    public int WarpX { get; private set; }
    public int WarpY { get; private set; }

    public void FireMouseMove(double x, double y) => _onMouseMove?.Invoke(x, y);
    public void FireKeyEvent(KeyEvent e) => _onKeyEvent?.Invoke(e);
    public void FireMouseButton(MouseButtonEvent e) => _onMouseButton?.Invoke(e);
    public void FireMouseScroll(MouseScrollEvent e) => _onMouseScroll?.Invoke(e);

    public void Reset()
    {
        IsOnVirtualScreen = false;
        HideCursorCalled = false;
        ShowCursorCalled = false;
        WarpX = 2560 / 2;
        WarpY = 1440 / 2;
    }

    public static List<DetectedScreen> GetAllScreens() => [new DetectedScreen(0, 0, 2560, 1440, null, null, null)];
    public bool IsAccessibilityTrusted() => true;

    public Task StartEventTap(
        Action<double, double> onMouseMove,
        Action<KeyEvent> onKeyEvent,
        Action<MouseButtonEvent> onMouseButton,
        Action<MouseScrollEvent> onMouseScroll)
    {
        _onMouseMove = onMouseMove;
        _onKeyEvent = onKeyEvent;
        _onMouseButton = onMouseButton;
        _onMouseScroll = onMouseScroll;
        WarpX = 2560 / 2;
        WarpY = 1440 / 2;
        return Task.CompletedTask;
    }

    public KeyRepeatSettings GetKeyRepeatSettings() => new(500, 33);
    public void StopEventTap() { }
    public void WarpCursor(int x, int y) { WarpX = x; WarpY = y; }
    public void HideCursor() { HideCursorCalled = true; }
    public void ShowCursor() { ShowCursorCalled = true; }
    public void Dispose() { }
}
