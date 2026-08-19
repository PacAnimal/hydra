using Hydra.Keyboard;
using Hydra.Mouse;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Linux;

// IPlatformInput implementation for headless Linux (no Xorg).
// reads raw events from /dev/input/event* devices, fires onMouseDelta for mouse movement.
// cursor/warp are no-ops — remote-only mode uses deltas, not absolute positions.
internal sealed class EvdevInputHandler(ILogger<EvdevInputHandler> log) : IPlatformInput
{
    // One entry per open device, in poll order. Keyboard and pointer are FLAGS, not alternatives:
    // a single evdev node can be both, and on a wireless receiver it usually is - see
    // DiscoverDevices for what picking just one of them silently broke.
    private readonly List<InputDeviceRole> _devices = [];
    private volatile bool _running;
    private volatile bool _grabbed;
    private Thread? _thread;
    private Action<double, double>? _onMouseDelta;
    private Action<KeyEvent>? _onKeyEvent;
    private Action<MouseButtonEvent>? _onMouseButton;
    private Action<MouseScrollEvent>? _onMouseScroll;
    private EvdevKeyResolver? _keyResolver;

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

    public bool IsAccessibilityTrusted() => true;

    public ValueTask HideCursor() => ValueTask.CompletedTask;   // no-op: headless
    public ValueTask ShowCursor() => ValueTask.CompletedTask;   // no-op: headless
    public void WarpCursor(int x, int y) { }          // no-op: remote-only uses deltas

    // evdev is headless/remote-only — no local screen, no OS window snapping to worry about
    public bool AnyMouseButtonHeld() => false;

    public Task StartEventTap(
        Action<double, double> onMouseMove,
        Action<double, double>? onMouseDelta,
        Action<KeyEvent> onKeyEvent,
        Action<MouseButtonEvent> onMouseButton,
        Action<MouseScrollEvent> onMouseScroll,
        Action? onLocalActivity = null)
    {
        // StartEventTap can be called more than once on the same instance: this handler is a DI
        // singleton and the self-updater restarts the host in-process. Without releasing first,
        // DiscoverDevices appends a SECOND set of fds to the same lists - the old set still holds
        // EVIOCGRAB, every grab on the new set fails with EBUSY, and input silently goes nowhere
        // while the process looks perfectly healthy.
        ReleaseDevices();

        _onMouseDelta = onMouseDelta;
        _onKeyEvent = onKeyEvent;
        _onMouseButton = onMouseButton;
        _onMouseScroll = onMouseScroll;

        var xkb = LinuxInputConfig.ResolveXkb();
        _keyResolver?.Dispose();   // a re-tap would otherwise leak the previous xkb context/keymap
        _keyResolver = new EvdevKeyResolver(xkb);
        log.LogInformation("Keyboard layout: {Layout} model: {Model}{Variant}",
            xkb.Layout, xkb.Model, xkb.Variant is null ? "" : $" variant: {xkb.Variant}");

        DiscoverDevices();

        if (_devices.Count == 0)
            throw new InvalidOperationException("No input devices found in /dev/input/. Check permissions (user may need to be in 'input' group).");

        log.LogInformation("Found {K} keyboard(s), {M} mouse/pointer device(s)",
            _devices.Count(d => d.Keyboard), _devices.Count(d => d.Pointer));

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
            _ = EvdevNativeMethods.close(device.Fd);
        log.LogDebug("Released {Count} input device(s)", _devices.Count);
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
            var fd = EvdevNativeMethods.open(path, EvdevNativeMethods.O_RDONLY | EvdevNativeMethods.O_NONBLOCK | EvdevNativeMethods.O_CLOEXEC);
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
            var isKeyboard = hasKey
                && EvdevNativeMethods.ioctl_bit(fd, EvdevNativeMethods.EVIOCGBIT_EV_KEY, keyBuf) >= 0
                && EvdevNativeMethods.TestBit(keyBuf, EvdevNativeMethods.KEY_A);

            // mouse/pointer: supports EV_REL with X and Y axes
            var isPointer = hasRel
                && EvdevNativeMethods.ioctl_bit(fd, EvdevNativeMethods.EVIOCGBIT_EV_REL, relBuf) >= 0
                && EvdevNativeMethods.TestBit(relBuf, EvdevNativeMethods.REL_X)
                && EvdevNativeMethods.TestBit(relBuf, EvdevNativeMethods.REL_Y);

            // Both tests run and BOTH answers are kept. These roles are not exclusive: a wireless
            // receiver presents one node that reports letter keys and relative axes together, so
            // testing for a keyboard first and stopping there classified every such mouse as a
            // keyboard and read its motion from nothing at all. The symptom is a pointer that does
            // not move while typing works perfectly, and it is invisible in the device counts
            // unless a pointer-only device (a Bluetooth mouse, a trackpad) happens to be present
            // to make up the numbers.
            if (!isKeyboard && !isPointer)
            {
                _ = EvdevNativeMethods.close(fd);
                continue;
            }

            var scale = isPointer ? LinuxInputConfig.MouseScale(path) : 1.0;
            _devices.Add(new InputDeviceRole(fd, isKeyboard, isPointer, scale));

            if (isPointer && Math.Abs(scale - 1.0) > 0.001)
                log.LogInformation("Mouse {Path}: MOUSE_DPI {Dpi} -> delta scale {Scale:0.##}",
                    path, LinuxInputConfig.MouseDpi(path), scale);

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
            var ready = NativeMethods.poll(ref polls[0], (uint)polls.Length, 100);
            if (ready <= 0) continue;

            for (var i = 0; i < polls.Length; i++)
            {
                if ((polls[i].Revents & NativeMethods.POLLIN) == 0) continue;
                var device = _devices[i];   // polls is built from _devices, so the indices agree

                while (true)
                {
                    var ev = new InputEvent();
                    var r = EvdevNativeMethods.read(device.Fd, ref ev, (nuint)System.Runtime.InteropServices.Marshal.SizeOf<InputEvent>());
                    if (r <= 0) break;

                    // Dispatch on the EVENT, not on the device. A combo device delivers keystrokes
                    // and motion down one fd, so the device alone cannot say what an event is.
                    // HandleKeyboardEvent already forwards BTN_* to the button path, which is why
                    // a keyboard with buttons needs nothing extra here.
                    if (ev.Type == EvdevNativeMethods.EV_KEY)
                    {
                        if (device.Keyboard) HandleKeyboardEvent(ev);
                        else HandleButtonCode(ev.Code, ev.Value);
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

    private void HandleMouseEvent(InputEvent ev, double scale, ref double pendingDx, ref double pendingDy)
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
                            pendingDx += ev.Value * scale;
                            break;
                        case EvdevNativeMethods.REL_Y:
                            pendingDy += ev.Value * scale;
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
        foreach (var fd in _devices.Select(d => d.Fd))
        {
            var r = EvdevNativeMethods.ioctl_grab(fd, EvdevNativeMethods.EVIOCGRAB, grab ? 1 : 0);
            if (r >= 0) continue;
            // releasing a grab that was never taken fails with EINVAL on every device, which is
            // noise rather than a fault. A failed *grab* is worth shouting about: it usually means
            // something else holds the device.
            if (grab) log.LogWarning("EVIOCGRAB(True) failed on fd={Fd} — is another process holding the device?", fd);
            else log.LogDebug("EVIOCGRAB(False) failed on fd={Fd} (was not grabbed)", fd);
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
