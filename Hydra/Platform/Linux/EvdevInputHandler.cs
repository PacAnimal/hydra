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
    // how often the event loop looks for devices plugged in since it started
    private const int RescanMs = 2000;

    // open devices in poll order; one node can be both keyboard and pointer (a wireless receiver usually is).
    // The event loop adds and drops devices as they come and go, while the router grabs them from its own
    // thread, so every access goes through _devicesLock and readers take a snapshot.
    private readonly List<InputDeviceRole> _devices = [];
    private readonly Lock _devicesLock = new();
    // event nodes that are neither keyboard nor pointer, so a rescan does not reopen them every time
    private readonly HashSet<string> _ignored = [];
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
    private sealed record InputDeviceRole(string Path, int Fd, bool Keyboard, bool Pointer, double Scale);

    private InputDeviceRole[] Devices()
    {
        lock (_devicesLock) return [.. _devices];
    }

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
        _keyResolver = new EvdevKeyResolver(xkb);
        if (log.IsEnabled(LogLevel.Information))
            log.LogInformation("Keyboard layout: {Layout} model: {Model}{Variant}",
                xkb.Layout, xkb.Model, xkb.Variant is null ? "" : $" variant: {xkb.Variant}");

        DiscoverDevices();
        var found = Devices();

        if (found.Length == 0)
            throw new InvalidOperationException("No input devices found in /dev/input/. Check permissions (user may need to be in 'input' group).");

        if (log.IsEnabled(LogLevel.Information))
        {
            var keyboards = found.Count(d => d.Keyboard);
            var pointers = found.Count(d => d.Pointer);
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
        var devices = Devices();
        if (devices.Length == 0) return;

        if (_grabbed) SetGrab(false);
        foreach (var device in devices)
            _ = EvdevNativeMethods.Close(device.Fd);
        if (log.IsEnabled(LogLevel.Debug)) log.LogDebug("Released {Count} input {Devices}", devices.Length, Pluralize.Of(devices.Length, "device"));
        lock (_devicesLock)
        {
            _devices.Clear();
            _ignored.Clear();
        }
        _grabbed = false;
    }

    // opens every event node not already open (or known to be neither keyboard nor pointer) and returns the new
    // devices; at start that is all of them, later the ones plugged in since
    private List<InputDeviceRole> DiscoverDevices()
    {
        var added = new List<InputDeviceRole>();
        var paths = Directory.GetFiles("/dev/input", "event*").OrderBy(p => p).ToList();
        HashSet<string> known;
        lock (_devicesLock)
        {
            // a node that went away may come back as a different device under the same name
            _ignored.IntersectWith(paths);
            known = [.. _devices.Select(d => d.Path), .. _ignored];
        }
        var evTypeBuf = new byte[1];
        var keyBuf = new byte[96];
        var relBuf = new byte[2];

        foreach (var path in paths.Where(p => !known.Contains(p)))
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
                lock (_devicesLock) _ignored.Add(path);
                continue;
            }

            var scale = isPointer ? LinuxInputConfig.MouseScale(path) : 1.0;
            var device = new InputDeviceRole(path, fd, isKeyboard, isPointer, scale);
            lock (_devicesLock) _devices.Add(device);
            added.Add(device);

            if (isPointer && Math.Abs(scale - 1.0) > 0.001 && log.IsEnabled(LogLevel.Information))
                log.LogInformation("Mouse {Path}: MOUSE_DPI {Dpi} -> delta scale {Scale:0.##}",
                    path, LinuxInputConfig.MouseDpi(path), scale);

            if (log.IsEnabled(LogLevel.Debug))
                log.LogDebug("{Role}: {Path}",
                    isKeyboard && isPointer ? "Keyboard+Mouse" : isKeyboard ? "Keyboard" : "Mouse", path);
        }
        return added;
    }

    // a device that was unplugged (or re-enumerated) reports POLLHUP/POLLERR on every poll from then on; left in
    // the set it would spin the event loop, so it is closed and dropped
    private void DropDevice(InputDeviceRole device)
    {
        lock (_devicesLock) _devices.Remove(device);
        _ = EvdevNativeMethods.Close(device.Fd);
        if (log.IsEnabled(LogLevel.Information)) log.LogInformation("Input device removed: {Path}", device.Path);
    }

    // devices plugged in since the last look; while the input is away they are grabbed as the others were
    private void AddNewDevices()
    {
        List<InputDeviceRole> added;
        try
        {
            added = DiscoverDevices();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }
        if (added.Count == 0) return;
        foreach (var device in added)
            if (log.IsEnabled(LogLevel.Information))
                log.LogInformation("Input device added: {Path} ({Role})", device.Path,
                    device.Keyboard && device.Pointer ? "keyboard+mouse" : device.Keyboard ? "keyboard" : "mouse");
        if (_grabbed) GrabDevices(added, true);
    }

    private void EventLoop()
    {
        var devices = Devices();
        var polls = devices.Select(d => new PollFd { Fd = d.Fd, Events = NativeMethods.POLLIN }).ToArray();
        var nextScan = Environment.TickCount64 + RescanMs;

        // accumulated mouse deltas between SYN reports
        double pendingDx = 0, pendingDy = 0;

        while (_running)
        {
            if (Environment.TickCount64 >= nextScan)
            {
                nextScan = Environment.TickCount64 + RescanMs;
                AddNewDevices();
            }

            // the set changed (a device came or went): poll the new one
            var current = Devices();
            if (!current.SequenceEqual(devices))
            {
                devices = current;
                polls = devices.Select(d => new PollFd { Fd = d.Fd, Events = NativeMethods.POLLIN }).ToArray();
            }
            if (polls.Length == 0)
            {
                Thread.Sleep(100);
                continue;
            }

            var ready = NativeMethods.Poll(ref polls[0], (uint)polls.Length, 100);
            if (ready <= 0) continue;

            for (var i = 0; i < polls.Length; i++)
            {
                var device = devices[i];   // polls is built from devices, so the indices agree
                if ((polls[i].Revents & (NativeMethods.POLLERR | NativeMethods.POLLHUP | NativeMethods.POLLNVAL)) != 0)
                {
                    DropDevice(device);
                    continue;
                }
                if ((polls[i].Revents & NativeMethods.POLLIN) == 0) continue;

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

    private void SetGrab(bool grab) => GrabDevices(Devices(), grab);

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
