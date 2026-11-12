using Cathedral.Utils;
using Hydra.Keyboard;
using Hydra.Mouse;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Linux;

// IPlatformInput implementation for headless Linux (no Xorg).
// reads raw events from /dev/input/event* devices, fires onMouseDelta for mouse movement.
// cursor/warp are no-ops — remote-only mode uses deltas, not absolute positions.
internal sealed class EvdevInputHandler(ILogger<EvdevInputHandler> log) : IPlatformInput
{
    // open devices in poll order; one node can be both keyboard and pointer (a wireless receiver usually is)
    private readonly List<InputDeviceRole> _devices = [];
    private volatile bool _running;
    private volatile bool _grabbed;
    private Thread? _thread;
    private Action<double, double>? _onMouseDelta;
    private Action<KeyEvent>? _onKeyEvent;
    private Action<MouseButtonEvent>? _onMouseButton;
    private Action<MouseScrollEvent>? _onMouseScroll;
    private EvdevKeyResolver? _keyResolver;
    // set while a grab waits for the keys held at its start to go up (DeferKeyboardGrabWhileHeld)
    private volatile bool _keyboardGrabPending;

    // A desktop session (Hyprland) also reads these devices. A keyboard grabbed with keys down never shows the
    // compositor their ups, so its modifiers would stick: the keyboards' grab then waits until every key is up.
    internal bool DeferKeyboardGrabWhileHeld { get; init; }

    // An open device and what it is for. Scale is the udev hwdb MOUSE_DPI delta multiplier, 1.0
    // when the device has no entry or is not a pointer at all.
    private sealed record InputDeviceRole(int Fd, bool Keyboard, bool Pointer, double Scale);

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

    public ValueTask HideCursor() => ValueTask.CompletedTask;   // no-op: headless
    public ValueTask ShowCursor() => ValueTask.CompletedTask;   // no-op: headless
    public void WarpCursor(int x, int y) { }          // no-op: remote-only uses deltas

    public bool RecentresItself => false;

    // evdev is headless/remote-only — no local screen, no OS window snapping to worry about
    public bool AnyMouseButtonHeld() => false;
    public event Action? CaptureFailed { add { } remove { } }
    public event Action? KeyboardCapturedLate { add { } remove { } }
    public bool SeesSwallowed(MouseButton button) => false;

    public Task StartEventTap(
        Action<double, double> onMouseMove,
        Action<double, double>? onMouseDelta,
        Action<KeyEvent> onKeyEvent,
        Action<MouseButtonEvent> onMouseButton,
        Action<MouseScrollEvent> onMouseScroll,
        Action? onLocalActivity = null)
    {
        // a re-tap (the host restarts in-process) releases the old fds first: they would keep EVIOCGRAB,
        // and every grab on the new set would fail with EBUSY
        ReleaseDevices();

        _onMouseDelta = onMouseDelta;
        _onKeyEvent = onKeyEvent;
        _onMouseButton = onMouseButton;
        _onMouseScroll = onMouseScroll;

        var xkb = LinuxInputConfig.ResolveXkb();
        _keyResolver?.Dispose();   // a re-tap would otherwise leak the previous xkb context/keymap
        try
        {
            _keyResolver = new EvdevKeyResolver(xkb);
        }
        catch (InvalidOperationException) when (xkb.Options != null)
        {
            // an option only the desktop's own xkb files define (Omarchy's compose option) fails to compile here
            log.LogWarning("Keyboard options '{Options}' did not compile; using the layout without them", xkb.Options);
            xkb = xkb with { Options = null };
            _keyResolver = new EvdevKeyResolver(xkb);
        }
        if (log.IsEnabled(LogLevel.Information))
            log.LogInformation("Keyboard layout: {Layout} model: {Model}{Variant}",
                xkb.Layout, xkb.Model, xkb.Variant is null ? "" : $" variant: {xkb.Variant}");

        DiscoverDevices();

        if (_devices.Count == 0)
            throw new InvalidOperationException("No input devices found in /dev/input/. Check permissions (user may need to be in 'input' group).");

        if (log.IsEnabled(LogLevel.Information))
        {
            var keyboards = _devices.Count(d => d.Keyboard);
            var pointers = _devices.Count(d => d.Pointer);
            log.LogInformation("Found {K} {Keyboards}, {M} mouse/pointer {Devices}",
                keyboards, Pluralize.Of(keyboards, "keyboard"), pointers, Pluralize.Of(pointers, "device"));
        }

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

    // stops the event loop and closes every device fd. Safe to call when nothing is open.
    private void ReleaseDevices()
    {
        StopEventTap();   // stop polling before closing the fds the loop polls
        if (_devices.Count == 0) return;

        if (_grabbed) SetGrab(false);
        foreach (var device in _devices)
            _ = EvdevNativeMethods.Close(device.Fd);
        if (log.IsEnabled(LogLevel.Debug)) log.LogDebug("Released {Count} input {Devices}", _devices.Count, Pluralize.Of(_devices.Count, "device"));
        _devices.Clear();
        _grabbed = false;
    }

    private void DiscoverDevices()
    {
        var paths = Directory.GetFiles("/dev/input", "event*").OrderBy(p => p);
        var evTypeBuf = new byte[1];
        var keyBuf = new byte[96];
        var relBuf = new byte[2];

        foreach (var path in paths)
        {
            var fd = EvdevNativeMethods.Open(path, EvdevNativeMethods.O_RDONLY | EvdevNativeMethods.O_NONBLOCK | EvdevNativeMethods.O_CLOEXEC);
            if (fd < 0) continue;

            // check which event types the device supports
            if (EvdevNativeMethods.ioctl_bit(fd, EvdevNativeMethods.EVIOCGBIT_EV, evTypeBuf) < 0)
            {
                _ = EvdevNativeMethods.Close(fd);
                continue;
            }

            var hasKey = TestBit(evTypeBuf, EvdevNativeMethods.EV_KEY);
            var hasRel = TestBit(evTypeBuf, EvdevNativeMethods.EV_REL);

            // keyboard: supports EV_KEY with letter keys
            var isKeyboard = hasKey
                && EvdevNativeMethods.ioctl_bit(fd, EvdevNativeMethods.EVIOCGBIT_EV_KEY, keyBuf) >= 0
                && TestBit(keyBuf, EvdevNativeMethods.KEY_A);

            // mouse/pointer: supports EV_REL with X and Y axes
            var isPointer = hasRel
                && EvdevNativeMethods.ioctl_bit(fd, EvdevNativeMethods.EVIOCGBIT_EV_REL, relBuf) >= 0
                && TestBit(relBuf, EvdevNativeMethods.REL_X)
                && TestBit(relBuf, EvdevNativeMethods.REL_Y);

            // keep both roles: a wireless receiver's one node reports letter keys and relative axes together
            if (!isKeyboard && !isPointer)
            {
                _ = EvdevNativeMethods.Close(fd);
                continue;
            }

            var scale = isPointer ? LinuxInputConfig.MouseScale(path) : 1.0;
            _devices.Add(new InputDeviceRole(fd, isKeyboard, isPointer, scale));

            if (isPointer && Math.Abs(scale - 1.0) > 0.001 && log.IsEnabled(LogLevel.Information))
                log.LogInformation("Mouse {Path}: MOUSE_DPI {Dpi} -> delta scale {Scale:0.##}",
                    path, LinuxInputConfig.MouseDpi(path), scale);

            if (log.IsEnabled(LogLevel.Debug))
                log.LogDebug("{Role}: {Path}",
                    isKeyboard && isPointer ? "Keyboard+Mouse" : isKeyboard ? "Keyboard" : "Mouse", path);
        }
    }

    private void EventLoop()
    {
        var polls = _devices.Select(d => new PollFd { Fd = d.Fd, Events = NativeMethods.POLLIN }).ToArray();

        // accumulated mouse deltas between SYN reports
        double pendingDx = 0, pendingDy = 0;

        while (_running)
        {
            var ready = NativeMethods.Poll(ref polls[0], (uint)polls.Length, 100);
            if (ready <= 0) continue;

            for (var i = 0; i < polls.Length; i++)
            {
                if ((polls[i].Revents & NativeMethods.POLLIN) == 0) continue;
                var device = _devices[i];   // polls is built from _devices, so the indices agree

                while (true)
                {
                    var ev = new InputEvent();
                    var r = EvdevNativeMethods.Read(device.Fd, ref ev, (nuint)System.Runtime.InteropServices.Marshal.SizeOf<InputEvent>());
                    if (r <= 0) break;

                    // dispatch on the event, not the device: a combo device sends keys and motion down one fd
                    if (ev.Type == EvdevNativeMethods.EV_KEY)
                    {
                        if (device.Keyboard) HandleKeyboardEvent(ev);
                        else HandleButtonCode(ev.Code, ev.Value);
                        if (ev.Value == 0 && _keyboardGrabPending) TryCompleteKeyboardGrab();
                    }
                    else if (device.Pointer)
                    {
                        HandleMouseEvent(ev, device.Scale, ref pendingDx, ref pendingDy);
                    }
                }
            }
        }
    }

    private void HandleKeyboardEvent(InputEvent ev)
    {
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

    private void HandleMouseEvent(InputEvent ev, double scale, ref double pendingDx, ref double pendingDy)
    {
        switch (ev.Type)
        {
            case EvdevNativeMethods.EV_REL:
                {
                    switch (ev.Code)
                    {
                        case EvdevNativeMethods.REL_X:
                            pendingDx += ev.Value * scale;
                            break;
                        case EvdevNativeMethods.REL_Y:
                            pendingDy += ev.Value * scale;
                            break;
                        case EvdevNativeMethods.REL_WHEEL:
                            _onMouseScroll?.Invoke(new MouseScrollEvent(0, (short)(ev.Value * MouseScrollEvent.Notch)));
                            break;
                        case EvdevNativeMethods.REL_HWHEEL:
                            _onMouseScroll?.Invoke(new MouseScrollEvent((short)(ev.Value * MouseScrollEvent.Notch), 0));
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

    // reads bit n of an EVIOCGBIT capability bitmap
    private static bool TestBit(byte[] bits, int n) => n / 8 < bits.Length && (bits[n / 8] & (1 << (n % 8))) != 0;

    // whether any keyboard key (codes below the button range) is down on any keyboard, from the kernel's own state
    internal bool KeysHeld() => _devices.Any(d => d.Keyboard && AnyBitSet(d.Fd, 1, EvdevNativeMethods.BTN_MISC));

    // whether a mouse button is down on any pointer, from the kernel's state: it sees through our own grab
    internal bool ButtonsHeld() => _devices.Any(d => d.Pointer && AnyBitSet(d.Fd, EvdevNativeMethods.BTN_LEFT, EvdevNativeMethods.BTN_EXTRA + 1));

    private static bool AnyBitSet(int fd, int from, int to)
    {
        var state = new byte[96];
        if (EvdevNativeMethods.ioctl_bit(fd, EvdevNativeMethods.EVIOCGKEY, state) < 0) return false;
        for (var code = from; code < to; code++)
            if (TestBit(state, code)) return true;
        return false;
    }

    private void TryCompleteKeyboardGrab()
    {
        if (!_grabbed || KeysHeld()) return;
        _keyboardGrabPending = false;
        GrabDevices(_devices.Where(d => d.Keyboard), true);
        if (log.IsEnabled(LogLevel.Debug)) log.LogDebug("Keys released: keyboard grab taken");
    }

    private void SetGrab(bool grab)
    {
        if (grab && DeferKeyboardGrabWhileHeld && KeysHeld())
        {
            _keyboardGrabPending = true;
            GrabDevices(_devices.Where(d => !d.Keyboard), true);
            if (log.IsEnabled(LogLevel.Debug)) log.LogDebug("Keys held: keyboard grab waits for their release");
            return;
        }
        _keyboardGrabPending = false;
        GrabDevices(_devices, grab);
    }

    private void GrabDevices(IEnumerable<InputDeviceRole> devices, bool grab)
    {
        foreach (var fd in devices.Select(d => d.Fd))
        {
            var r = EvdevNativeMethods.ioctl_grab(fd, EvdevNativeMethods.EVIOCGRAB, grab ? 1 : 0);
            if (r >= 0) continue;
            // releasing a grab that was never taken fails with EINVAL on every device, which is
            // noise rather than a fault. A failed *grab* is worth shouting about: it usually means
            // something else holds the device.
            if (grab) log.LogWarning("EVIOCGRAB(True) failed on fd={Fd} — is another process holding the device?", fd);
            else if (log.IsEnabled(LogLevel.Debug)) log.LogDebug("EVIOCGRAB(False) failed on fd={Fd} (was not grabbed)", fd);
        }
    }

    public ValueTask DisposeAsync()
    {
        ReleaseDevices();
        _keyResolver?.Dispose();
        _keyResolver = null;
        return ValueTask.CompletedTask;
    }
}
