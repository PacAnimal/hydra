using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Screen;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Linux;

// Master input for a Hyprland (Wayland) session. Wayland gives no client a global pointer or keyboard, so input is
// read from evdev (/dev/input, the input group) like the headless master, and the compositor is asked over its IPC
// socket where the cursor is, to move it, and to hide it:
//   * at home, devices stay shared with Hyprland; pointer motion triggers a cursor position query (throttled, since
//     each one runs on the compositor's main loop) whose answer feeds edge detection
//   * on a remote screen, EVIOCGRAB takes the devices from Hyprland and their raw deltas drive the remote cursor
internal sealed class HyprlandInputHandler : IPlatformInput
{
    // at most one position query per this many milliseconds: ~125 Hz, under a frame at any refresh rate
    private const int QueryIntervalMs = 8;

    private readonly ILogger<HyprlandInputHandler> _log;
    private readonly EvdevInputHandler _evdev;
    private readonly AutoResetEvent _moved = new(false);
    private Thread? _positionThread;
    private volatile bool _running;
    private Action<double, double>? _onMouseMove;
    private Action<double, double>? _onMouseDelta;
    private bool _cursorHidden;
    private ScreenPoint? _lastWarp;

    public HyprlandInputHandler(ILogger<HyprlandInputHandler> log, ILogger<EvdevInputHandler> evdevLog)
    {
        _log = log;
        _evdev = new EvdevInputHandler(evdevLog) { DeferKeyboardGrabWhileHeld = true };
    }

    public bool IsOnVirtualScreen
    {
        get => _evdev.IsOnVirtualScreen;
        set
        {
            _lastWarp = null;
            _evdev.IsOnVirtualScreen = value;
        }
    }

    public async Task StartEventTap(
        Action<double, double> onMouseMove,
        Action<double, double>? onMouseDelta,
        Action<KeyEvent> onKeyEvent,
        Action<MouseButtonEvent> onMouseButton,
        Action<MouseScrollEvent> onMouseScroll,
        Action? onLocalActivity = null)
    {
        _onMouseMove = onMouseMove;
        _onMouseDelta = onMouseDelta;

        await _evdev.StartEventTap((_, _) => { }, OnDelta, onKeyEvent, onMouseButton, onMouseScroll, onLocalActivity);

        _running = true;
        _positionThread = new Thread(PositionLoop) { Name = "HydraHyprlandCursor", IsBackground = true };
        _positionThread.Start();

        // the router needs a first position to know which screen is active, before the mouse has moved
        _moved.Set();
        _log.LogInformation("Hyprland session: input from evdev, cursor from Hyprland IPC");
    }

    private void OnDelta(double dx, double dy)
    {
        if (_evdev.IsOnVirtualScreen) _onMouseDelta?.Invoke(dx, dy);
        else _moved.Set();
    }

    // one query per wake, then a pause: motion during the pause re-arms the event, so the latest position is always
    // read once the pause ends (a trailing query), and a burst of samples costs the compositor one query per interval
    private void PositionLoop()
    {
        while (_running)
        {
            if (!_moved.WaitOne(100)) continue;
            if (_evdev.IsOnVirtualScreen) continue;
            if (HyprlandIpc.CursorPos() is { } pos && !_evdev.IsOnVirtualScreen)
                _onMouseMove?.Invoke(pos.X, pos.Y);
            Thread.Sleep(QueryIntervalMs);
        }
    }

    public void StopEventTap()
    {
        _running = false;
        _positionThread?.Join(TimeSpan.FromSeconds(1));
        _positionThread = null;
        _evdev.StopEventTap();
    }

    public void WarpCursor(int x, int y)
    {
        // devices grabbed, the cursor cannot move, so a repeat of the last warp would only cost the compositor a request
        var to = new ScreenPoint(x, y);
        if (_evdev.IsOnVirtualScreen && _lastWarp == to) return;
        _lastWarp = to;
        HyprlandIpc.MoveCursor(x, y);
    }

    public ScreenPoint? GetCursorPosition() =>
        HyprlandIpc.CursorPos() is { } p ? new ScreenPoint((int)p.X, (int)p.Y) : null;

    public ValueTask HideCursor()
    {
        if (_cursorHidden) return ValueTask.CompletedTask;
        HyprlandIpc.SetCursorInvisible(true);
        _cursorHidden = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask ShowCursor()
    {
        if (!_cursorHidden) return ValueTask.CompletedTask;
        HyprlandIpc.SetCursorInvisible(false);
        _cursorHidden = false;
        return ValueTask.CompletedTask;
    }

    public bool RecentresItself => false;

    // the kernel's button state sees through our grab
    public bool AnyMouseButtonHeld() => _evdev.ButtonsHeld();
    public bool SeesSwallowed(MouseButton button) => true;
    public bool AnyKeyHeld() => _evdev.KeysHeld();

    public event Action? CaptureFailed { add { } remove { } }
    public event Action? KeyboardCapturedLate { add { } remove { } }

    public async ValueTask DisposeAsync()
    {
        StopEventTap();
        await _evdev.DisposeAsync();
        // never leave the desktop without a cursor
        HyprlandIpc.SetCursorInvisible(false);
        _cursorHidden = false;
        _moved.Dispose();
    }
}
