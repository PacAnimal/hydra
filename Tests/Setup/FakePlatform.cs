using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Platform;
using Hydra.Screen;

namespace Tests.Setup;

public sealed class FakePlatform : IPlatformInput, ICursorHider
{
    private Action<double, double>? _onMouseMove;
    private Action<double, double>? _onMouseDelta;
    private Action<KeyEvent>? _onKeyEvent;
    private Action<MouseButtonEvent>? _onMouseButton;
    private Action<MouseScrollEvent>? _onMouseScroll;

    // the order of hider, flag and warp calls, which on a return decides whether a hider pin can land last
    public List<string> Calls { get; } = [];
    // runs inside WarpCursor, for a test that acts while a warp is in flight
    public Action? OnWarp { get; set; }

    private bool _isOnVirtualScreen;

    public bool IsOnVirtualScreen
    {
        get => _isOnVirtualScreen;
        set
        {
            _isOnVirtualScreen = value;
            Calls.Add($"flag:{value}");
        }
    }
    public bool HideCursorCalled { get; set; }
    public bool ShowCursorCalled { get; set; }
    public int WarpX { get; private set; }
    public int WarpCount { get; private set; }

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
        Calls.Clear();
    }

    public static List<DetectedScreen> GetAllScreens() => [new DetectedScreen(0, 0, 2560, 1440, null, null, null)];
    public bool IsAccessibilityTrusted() => true;

    public Task StartEventTap(
        Action<double, double> onMouseMove,
        Action<double, double>? onMouseDelta,
        Action<KeyEvent> onKeyEvent,
        Action<MouseButtonEvent> onMouseButton,
        Action<MouseScrollEvent> onMouseScroll,
        Action? onLocalActivity = null)
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
    public bool CursorIsVisible { get; set; }
    public bool RecentresItself { get; set; }
    // the default for SeesSwallowed, less UnseenButtons
    public bool SeesSwallowedButtons { get; set; }
    // buttons hidden from AnyMouseButtonHeld even where it sees swallowed ones (Xorg's side buttons)
    public MouseButton[] UnseenButtons { get; set; } = [];
    public bool SeesSwallowed(MouseButton button) => SeesSwallowedButtons && !UnseenButtons.Contains(button);
    // how often the router asked, which on Xorg is a server round-trip
    public int ButtonQueries { get; private set; }

    bool IPlatformInput.AnyMouseButtonHeld()
    {
        ButtonQueries++;
        return AnyMouseButtonHeld;
    }
    public void StopEventTap() { }
    public void WarpCursor(int x, int y)
    {
        OnWarp?.Invoke();
        WarpX = x;
        WarpY = y;
        WarpCount++;
        Calls.Add("warp");
    }
    public ScreenPoint? GetCursorPosition() => null;
    // ICursorHider — what InputRouter calls
    void ICursorHider.Hide() { HideCursorCalled = true; Calls.Add("hide"); }
    void ICursorHider.Show() { ShowCursorCalled = true; Calls.Add("show"); }

    public ValueTask HideCursor() { HideCursorCalled = true; return ValueTask.CompletedTask; }
    public ValueTask ShowCursor() { ShowCursorCalled = true; return ValueTask.CompletedTask; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
