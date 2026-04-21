using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Platform;

namespace Tests.Setup;

public sealed class FakePlatform : IPlatformInput
{
    private Action<double, double>? _onMouseMove;
    private Action<double, double>? _onMouseDelta;
    private Action<KeyEvent>? _onKeyEvent;
    private Action<MouseButtonEvent>? _onMouseButton;
    private Action<MouseScrollEvent>? _onMouseScroll;

    public bool IsOnVirtualScreen { get; set; }
    public bool HideCursorCalled { get; set; }
    public bool ShowCursorCalled { get; set; }
    public int WarpX { get; private set; }
    public int WarpY { get; private set; }

    // set to InputRouter.FlushAsync to synchronize channel consumer after each Fire call
    public Func<Task>? AfterFireCallback { get; set; }

    private void Flush() => AfterFireCallback?.Invoke().GetAwaiter().GetResult();

    public void FireMouseMove(double x, double y) { _onMouseMove?.Invoke(x, y); Flush(); }
    public void FireMouseDelta(double dx, double dy) { _onMouseDelta?.Invoke(dx, dy); Flush(); }
    public void FireKeyEvent(KeyEvent e) { _onKeyEvent?.Invoke(e); Flush(); }
    public void FireMouseButton(MouseButtonEvent e) { _onMouseButton?.Invoke(e); Flush(); }
    public void FireMouseScroll(MouseScrollEvent e) { _onMouseScroll?.Invoke(e); Flush(); }

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
        Action<double, double>? onMouseDelta,
        Action<KeyEvent> onKeyEvent,
        Action<MouseButtonEvent> onMouseButton,
        Action<MouseScrollEvent> onMouseScroll)
    {
        _onMouseMove = onMouseMove;
        _onMouseDelta = onMouseDelta;
        _onKeyEvent = onKeyEvent;
        _onMouseButton = onMouseButton;
        _onMouseScroll = onMouseScroll;
        WarpX = 2560 / 2;
        WarpY = 1440 / 2;
        return Task.CompletedTask;
    }

    public bool AnyMouseButtonHeld { get; set; }
    bool IPlatformInput.AnyMouseButtonHeld() => AnyMouseButtonHeld;
    public KeyRepeatSettings GetKeyRepeatSettings() => new(500, 33);
    public void StopEventTap() { }
    public void WarpCursor(int x, int y) { WarpX = x; WarpY = y; }
    public Task HideCursor() { HideCursorCalled = true; return Task.CompletedTask; }
    public Task ShowCursor() { ShowCursorCalled = true; return Task.CompletedTask; }
    public void Dispose() { }
}
