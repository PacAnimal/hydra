using Hydra.Keyboard;
using Hydra.Mouse;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Linux;

// IPlatformInput implementation for headless Linux (no Xorg).
// reads raw events from /dev/input/event* devices, fires onMouseDelta for mouse movement.
// cursor/warp are no-ops — remote-only mode uses deltas, not absolute positions.
internal sealed class EvdevInputHandler(ILogger<EvdevInputHandler> log) : IPlatformInput
{
    private readonly List<int> _keyboardFds = [];
    private readonly List<int> _mouseFds = [];
    private volatile bool _running;
    private volatile bool _grabbed;
    private Thread? _thread;
    private Action<double, double>? _onMouseDelta;
    private Action<KeyEvent>? _onKeyEvent;
    private Action<MouseButtonEvent>? _onMouseButton;
    private Action<MouseScrollEvent>? _onMouseScroll;
    private EvdevKeyResolver? _keyResolver;

    public bool IsOnVirtualScreen
    {
        get => _grabbed;
        set
        {
            if (_grabbed == value) return;
            _grabbed = value;
            if (value) _keyResolver?.Reset();  // clear stale state from previous grab session
            SetGrab(value);
        }
    }

    public bool IsAccessibilityTrusted() => true;

    public Task HideCursor() => Task.CompletedTask;   // no-op: headless
    public Task ShowCursor() => Task.CompletedTask;   // no-op: headless
    public void WarpCursor(int x, int y) { }          // no-op: remote-only uses deltas

    // evdev is headless/remote-only — no local screen, no OS window snapping to worry about
    public bool AnyMouseButtonHeld() => false;

    public KeyRepeatSettings GetKeyRepeatSettings()
    {
        // try reading repeat settings from the first keyboard device
        foreach (var fd in _keyboardFds)
        {
            var rep = new EvdevRepeatSettings();
            if (EvdevNativeMethods.ioctl_rep(fd, EvdevNativeMethods.EVIOCGREP, ref rep) == 0 && rep.DelayMs > 0)
                return new KeyRepeatSettings((int)rep.DelayMs, (int)rep.RateMs);
        }
        return new KeyRepeatSettings(500, 33);
    }

    public Task StartEventTap(
        Action<double, double> onMouseMove,
        Action<double, double>? onMouseDelta,
        Action<KeyEvent> onKeyEvent,
        Action<MouseButtonEvent> onMouseButton,
        Action<MouseScrollEvent> onMouseScroll)
    {
        _onMouseDelta = onMouseDelta;
        _onKeyEvent = onKeyEvent;
        _onMouseButton = onMouseButton;
        _onMouseScroll = onMouseScroll;

        var layout = Environment.GetEnvironmentVariable("XKB_DEFAULT_LAYOUT") ?? "us";
        _keyResolver = new EvdevKeyResolver(layout);
        log.LogInformation("Keyboard layout: {Layout}", layout);

        DiscoverDevices();

        if (_keyboardFds.Count == 0 && _mouseFds.Count == 0)
            throw new InvalidOperationException("No input devices found in /dev/input/. Check permissions (user may need to be in 'input' group).");

        log.LogInformation("Found {K} keyboard(s), {M} mouse/pointer device(s)", _keyboardFds.Count, _mouseFds.Count);

        _running = true;
        _thread = new Thread(EventLoop) { Name = "HydraEvdevEventLoop", IsBackground = true };
        _thread.Start();

        return Task.CompletedTask;
    }

    public void StopEventTap()
    {
        _running = false;
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    private void DiscoverDevices()
    {
        var paths = Directory.GetFiles("/dev/input", "event*").OrderBy(p => p);
        var evTypeBuf = new byte[1];
        var keyBuf = new byte[96];
        var relBuf = new byte[2];

        foreach (var path in paths)
        {
            var fd = EvdevNativeMethods.open(path, EvdevNativeMethods.O_RDONLY | EvdevNativeMethods.O_NONBLOCK);
            if (fd < 0) continue;

            // check which event types the device supports
            if (EvdevNativeMethods.ioctl_bit(fd, EvdevNativeMethods.EVIOCGBIT_EV, evTypeBuf) < 0)
            {
                _ = EvdevNativeMethods.close(fd);
                continue;
            }

            var hasKey = EvdevNativeMethods.TestBit(evTypeBuf, EvdevNativeMethods.EV_KEY);
            var hasRel = EvdevNativeMethods.TestBit(evTypeBuf, EvdevNativeMethods.EV_REL);

            // keyboard: supports EV_KEY with letter keys
            if (hasKey && EvdevNativeMethods.ioctl_bit(fd, EvdevNativeMethods.EVIOCGBIT_EV_KEY, keyBuf) == 0
                && EvdevNativeMethods.TestBit(keyBuf, EvdevNativeMethods.KEY_A))
            {
                _keyboardFds.Add(fd);
                log.LogDebug("Keyboard: {Path}", path);
                continue;
            }

            // mouse/pointer: supports EV_REL with X and Y axes
            if (hasRel && EvdevNativeMethods.ioctl_bit(fd, EvdevNativeMethods.EVIOCGBIT_EV_REL, relBuf) == 0
                && EvdevNativeMethods.TestBit(relBuf, EvdevNativeMethods.REL_X)
                && EvdevNativeMethods.TestBit(relBuf, EvdevNativeMethods.REL_Y))
            {
                _mouseFds.Add(fd);
                log.LogDebug("Mouse: {Path}", path);
                continue;
            }

            _ = EvdevNativeMethods.close(fd);
        }
    }

    private void EventLoop()
    {
        var allFds = _keyboardFds.Concat(_mouseFds).ToArray();
        var polls = allFds.Select(fd => new PollFd { Fd = fd, Events = NativeMethods.POLLIN }).ToArray();

        // accumulated mouse deltas between SYN reports
        double pendingDx = 0, pendingDy = 0;

        while (_running)
        {
            var ready = NativeMethods.poll(ref polls[0], (uint)polls.Length, 100);
            if (ready <= 0) continue;

            for (var i = 0; i < polls.Length; i++)
            {
                if ((polls[i].Revents & NativeMethods.POLLIN) == 0) continue;
                var fd = polls[i].Fd;
                var isKeyboard = i < _keyboardFds.Count;

                while (true)
                {
                    var ev = new InputEvent();
                    var r = EvdevNativeMethods.read(fd, ref ev, (nuint)System.Runtime.InteropServices.Marshal.SizeOf<InputEvent>());
                    if (r <= 0) break;

                    if (isKeyboard)
                        HandleKeyboardEvent(ev);
                    else
                        HandleMouseEvent(ev, ref pendingDx, ref pendingDy);
                }
            }
        }
    }

    private void HandleKeyboardEvent(InputEvent ev)
    {
        if (ev.Type != EvdevNativeMethods.EV_KEY) return;
        if (_keyResolver == null) return;

        var code = ev.Code;
        var value = ev.Value;

        // mouse buttons coming from a keyboard device (e.g. touchpad buttons)
        if (code is >= EvdevNativeMethods.BTN_LEFT and <= EvdevNativeMethods.BTN_EXTRA)
        {
            HandleButtonCode(code, value);
            return;
        }

        var keyEvents = _keyResolver.Resolve(code, value);
        if (keyEvents is not null)
            foreach (var keyEvent in keyEvents)
                if (keyEvent is not null) _onKeyEvent?.Invoke(keyEvent);
    }

    private void HandleMouseEvent(InputEvent ev, ref double pendingDx, ref double pendingDy)
    {
        switch (ev.Type)
        {
            case EvdevNativeMethods.EV_KEY:
                HandleButtonCode(ev.Code, ev.Value);
                break;

            case EvdevNativeMethods.EV_REL:
                {
                    switch (ev.Code)
                    {
                        case EvdevNativeMethods.REL_X:
                            pendingDx += ev.Value;
                            break;
                        case EvdevNativeMethods.REL_Y:
                            pendingDy += ev.Value;
                            break;
                        case EvdevNativeMethods.REL_WHEEL:
                            _onMouseScroll?.Invoke(new MouseScrollEvent(0, (short)(ev.Value * 120)));
                            break;
                        case EvdevNativeMethods.REL_HWHEEL:
                            _onMouseScroll?.Invoke(new MouseScrollEvent((short)(ev.Value * 120), 0));
                            break;
                    }
                    break;
                }

            case EvdevNativeMethods.EV_SYN:
                // flush accumulated mouse deltas
                if (pendingDx != 0 || pendingDy != 0)
                {
                    _onMouseDelta?.Invoke(pendingDx, pendingDy);
                    pendingDx = 0;
                    pendingDy = 0;
                }
                break;
        }
    }

    private void HandleButtonCode(ushort code, int value)
    {
        if (value == 2) return;  // ignore repeat for buttons
        var button = code switch
        {
            EvdevNativeMethods.BTN_LEFT => MouseButton.Left,
            EvdevNativeMethods.BTN_RIGHT => MouseButton.Right,
            EvdevNativeMethods.BTN_MIDDLE => MouseButton.Middle,
            EvdevNativeMethods.BTN_SIDE => MouseButton.Extra1,
            _ => MouseButton.Extra2,
        };
        _onMouseButton?.Invoke(new MouseButtonEvent(button, value == 1));
    }

    private void SetGrab(bool grab)
    {
        var all = _keyboardFds.Concat(_mouseFds);
        foreach (var fd in all)
        {
            var r = EvdevNativeMethods.ioctl_grab(fd, EvdevNativeMethods.EVIOCGRAB, grab ? 1 : 0);
            if (r < 0)
                log.LogWarning("EVIOCGRAB({Grab}) failed on fd={Fd}", grab, fd);
        }
    }

    public void Dispose()
    {
        StopEventTap();
        _keyResolver?.Dispose();
        foreach (var fd in _keyboardFds.Concat(_mouseFds))
            _ = EvdevNativeMethods.close(fd);
        _keyboardFds.Clear();
        _mouseFds.Clear();
    }
}
