using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Screen;

namespace Hydra.Platform;

// no-op IPlatformInput for slave — delegates cursor ops to the output handler
internal sealed class SlavePlatformInput(ICursor cursor) : IPlatformInput
{
    public bool IsOnVirtualScreen { get; set; }

    public ValueTask HideCursor() => cursor.HideCursor();
    public ValueTask ShowCursor() => cursor.ShowCursor();
    public void WarpCursor(int x, int y) => cursor.WarpCursor(x, y);
    public ScreenPoint? GetCursorPosition() => cursor.GetCursorPosition();

    public Task StartEventTap(Action<double, double> onMouseMove, Action<double, double>? onMouseDelta,
        Action<KeyEvent> onKeyEvent, Action<MouseButtonEvent> onMouseButton, Action<MouseScrollEvent> onMouseScroll,
        Action? onLocalActivity = null)
        => Task.CompletedTask;
    public void StopEventTap() { }
    public bool RecentresItself => false;
    public bool AnyMouseButtonHeld() => false;
    public bool SeesSwallowed(MouseButton button) => false;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
