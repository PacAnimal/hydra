using Hydra.Keyboard;
using Hydra.Mouse;

namespace Hydra.Platform;

// no-op IPlatformInput for slave — delegates cursor ops to the output handler, disables local movement poll
internal sealed class SlavePlatformInput(ICursor cursor) : IPlatformInput
{
    public bool IsOnVirtualScreen { get; set; }

    public ValueTask HideCursor() => cursor.HideCursor();
    public ValueTask ShowCursor() => cursor.ShowCursor();
    public void WarpCursor(int x, int y) => cursor.WarpCursor(x, y);
    public (int X, int Y)? GetCursorPosition() => null;

    public Task StartEventTap(Action<double, double> onMouseMove, Action<double, double>? onMouseDelta,
        Action<KeyEvent> onKeyEvent, Action<MouseButtonEvent> onMouseButton, Action<MouseScrollEvent> onMouseScroll)
        => Task.CompletedTask;
    public void StopEventTap() { }
    public bool IsAccessibilityTrusted() => true;
    public KeyRepeatSettings GetKeyRepeatSettings() => new(500, 33);
    public bool AnyMouseButtonHeld() => false;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
