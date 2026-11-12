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
    // how often the event loop looks for devices plugged in since it started: a backstop while /dev/input is
    // watched, the only way when it cannot be
    private const int WatchedRescanMs = 10_000;
    private const int UnwatchedRescanMs = 2000;
    // how soon a node udev has not finished with is looked at again
    private const int PendingRescanMs = 100;
    // how long a new node waits for udev before it is opened anyway
    private const int UdevWaitMs = 1000;

    // open devices in poll order; one node can be both keyboard and pointer (a wireless receiver usually is).
    // The event loop adds and drops devices as they come and go, while the router grabs them from its own
    // thread: the set, its grab, every fd's close and the generation change together under _devicesLock, so a
    // device added mid-release is never left grabbed and a grab never reaches a closed fd.
    private readonly List<InputDevice> _devices = [];
    private readonly Lock _devicesLock = new();
    // bumped (under _devicesLock) on every change to _devices, so the event loop rebuilds its poll set only when it changed
    private int _devicesVersion;
    // bumped by every stop: a loop that outlived its stop's join, or its scan, adds nothing and exits
    private int _generation;
    private volatile bool _rescanDue;
    private volatile bool _grabbed;
    private Thread? _thread;
    private FileSystemWatcher? _watcher;
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

    // where the event nodes are; tests point it at a directory of links to devices they made
    internal string InputDir { get; init; } = "/dev/input";

    // An open device and what it is for. Scale is the udev hwdb MOUSE_DPI delta multiplier, 1.0
    // when the device has no entry or is not a pointer at all.
    private sealed class InputDevice(string path, int fd, bool keyboard, bool pointer, double scale)
    {
        public string Path { get; } = path;
        public int Fd { get; } = fd;
        public bool Keyboard { get; } = keyboard;
        public bool Pointer { get; } = pointer;
        public double Scale { get; } = scale;

        // key and button codes down here; released by hand when the device vanishes, since its own ups are then unreadable
        public HashSet<ushort> Held { get; } = [];

        // motion accumulated between SYN reports
        public double PendingDx { get; set; }
        public double PendingDy { get; set; }

        public string Role => Keyboard && Pointer ? "keyboard+mouse" : Keyboard ? "keyboard" : "mouse";
    }

    private InputDevice[] Devices()
    {
        lock (_devicesLock) return [.. _devices];
    }

    public bool IsOnVirtualScreen
    {
        get => _grabbed;
        set
        {
            lock (_devicesLock)
            {
                if (_grabbed == value) return;
                _grabbed = value;
                SetGrab(value);
            }
            if (value) _keyResolver?.Reset();  // clear stale state from previous grab session
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

        int generation;
        lock (_devicesLock) generation = _generation;
        var scans = new ScanState();
        var scan = DiscoverDevices(generation, scans, waitForUdev: false);

        // nodes there but none of them openable is a permissions problem; no keyboard or mouse yet is not
        if (scan.Added.Count == 0 && scan.Nodes > 0 && scan.Unopened == scan.Nodes)
            throw new InvalidOperationException("No input devices could be opened in /dev/input/. Check permissions (user may need to be in 'input' group).");

        if (scan.Added.Count == 0)
            log.LogWarning("No keyboard or mouse found in /dev/input/; waiting for one to be plugged in");
        else if (log.IsEnabled(LogLevel.Information))
        {
            var keyboards = scan.Added.Count(d => d.Keyboard);
            var pointers = scan.Added.Count(d => d.Pointer);
            log.LogInformation("Found {K} {Keyboards}, {M} mouse/pointer {Devices}",
                keyboards, Pluralize.Of(keyboards, "keyboard"), pointers, Pluralize.Of(pointers, "device"));
        }

        _watcher = WatchInputDir();
        var rescanMs = _watcher != null ? WatchedRescanMs : UnwatchedRescanMs;
        _thread = new Thread(() => EventLoop(generation, scans, rescanMs)) { Name = "HydraEvdevEventLoop", IsBackground = true };
        _thread.Start();

        return Task.CompletedTask;
    }

    public void StopEventTap()
    {
        lock (_devicesLock) _generation++;
        _watcher?.Dispose();
        _watcher = null;
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    // stops the event loop and closes every device fd. Safe to call when nothing is open.
    private void ReleaseDevices()
    {
        StopEventTap();   // stop polling before closing the fds the loop polls
        int count;
        lock (_devicesLock)
        {
            count = _devices.Count;
            if (_grabbed) GrabDevices(_devices, false);
            foreach (var device in _devices)
                _ = EvdevNativeMethods.Close(device.Fd);
            _devices.Clear();
            _devicesVersion++;
            _grabbed = false;
            _keyboardGrabPending = false;
        }
        if (count > 0 && log.IsEnabled(LogLevel.Debug)) log.LogDebug("Released {Count} input {Devices}", count, Pluralize.Of(count, "device"));
    }

    // inotify on /dev/input, so a plugged-in device is picked up at once rather than at the next periodic scan
    private FileSystemWatcher? WatchInputDir()
    {
        try
        {
            var watcher = new FileSystemWatcher(InputDir, "event*") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.Attributes };
            watcher.Created += (_, _) => _rescanDue = true;
            watcher.Changed += (_, _) => _rescanDue = true;
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            if (log.IsEnabled(LogLevel.Debug)) log.LogDebug(e, "Cannot watch {Dir}; new devices are found by the periodic scan", InputDir);
            return null;
        }
    }

    // Opens every event node that is not open yet and keeps the keyboards and pointers among them. A node that is
    // neither is remembered by the input device behind it, not its name: the kernel reuses a freed event number at
    // once. Opening a node wakes its device (a touchpad, a USB headset's buttons), so it is probed once, not on every
    // scan. A hotplugged node waits for udev (its permissions and hwdb properties) first; at start udev is long done.
    private DiscoveryResult DiscoverDevices(int generation, ScanState scans, bool waitForUdev)
    {
        var added = new List<InputDevice>();
        var paths = Directory.GetFiles(InputDir, "event*").OrderBy(p => p, StringComparer.Ordinal).ToList();
        HashSet<string> open;
        lock (_devicesLock) open = [.. _devices.Select(d => d.Path)];
        var now = Environment.TickCount64;
        scans.Forget(paths);

        var evTypeBuf = new byte[1];
        var keyBuf = new byte[96];
        var relBuf = new byte[2];
        var unopened = 0;
        var pending = false;

        foreach (var path in paths.Where(p => !open.Contains(p)))
        {
            var id = LinuxInputConfig.InputDeviceId(path);
            if (id != null && scans.Ignored.TryGetValue(path, out var ignored) && ignored == id) continue;

            if (waitForUdev)
            {
                if (!scans.PendingSince.TryGetValue(path, out var since)) scans.PendingSince[path] = since = now;
                if (!LinuxInputConfig.UdevSettled(path) && now - since < UdevWaitMs)
                {
                    pending = true;
                    continue;
                }
            }

            var fd = EvdevNativeMethods.Open(path, EvdevNativeMethods.O_RDONLY | EvdevNativeMethods.O_NONBLOCK | EvdevNativeMethods.O_CLOEXEC);
            if (fd < 0)
            {
                unopened++;
                continue;
            }

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
                if (id != null) scans.Ignored[path] = id;
                if (log.IsEnabled(LogLevel.Debug)) log.LogDebug("Neither keyboard nor mouse: {Path}", path);
                scans.PendingSince.Remove(path);
                continue;
            }

            var scale = isPointer ? LinuxInputConfig.MouseScale(path) : 1.0;
            var device = new InputDevice(path, fd, isKeyboard, isPointer, scale);
            lock (_devicesLock)
            {
                // a loop that outlived its stop: the fds now belong to whoever started after it
                if (generation != _generation)
                {
                    _ = EvdevNativeMethods.Close(fd);
                    break;
                }
                _devices.Add(device);
                _devicesVersion++;
                // grabbed in the same section that adds it, so a release taking the lock next releases it too
                if (_grabbed && !(device.Keyboard && _keyboardGrabPending)) GrabDevices([device], true);
            }
            added.Add(device);
            scans.PendingSince.Remove(path);

            if (isPointer && Math.Abs(scale - 1.0) > 0.001 && log.IsEnabled(LogLevel.Information))
                log.LogInformation("Mouse {Path}: MOUSE_DPI {Dpi} -> delta scale {Scale:0.##}",
                    path, LinuxInputConfig.MouseDpi(path), scale);

            if (log.IsEnabled(LogLevel.Debug)) log.LogDebug("{Role}: {Path}", device.Role, path);
        }
        return new DiscoveryResult(added, paths.Count, unopened, pending);
    }

    // A device that was unplugged (or re-enumerated) reports POLLHUP/POLLERR on every poll from then on; left in
    // the set it would spin the event loop, so it is dropped, and what it held down is released: the kernel queues
    // those ups, but a dead device's read fails before returning them. POLLNVAL means the fd is closed already.
    private void DropDevice(InputDevice device, bool closeFd)
    {
        lock (_devicesLock)
        {
            // not in the set: a release took it, and its fd number may already be someone else's
            if (!_devices.Remove(device)) return;
            _devicesVersion++;
            if (closeFd) _ = EvdevNativeMethods.Close(device.Fd);
        }
        foreach (var code in device.Held.ToList())
            HandleKeyCode(device, code, 0);
        if (log.IsEnabled(LogLevel.Information)) log.LogInformation("Input device removed: {Path}", device.Path);
    }

    // devices plugged in since the last look; true while one waits for udev
    private bool AddNewDevices(int generation, ScanState scans)
    {
        DiscoveryResult scan;
        try
        {
            scan = DiscoverDevices(generation, scans, waitForUdev: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        if (log.IsEnabled(LogLevel.Information))
            foreach (var device in scan.Added)
                log.LogInformation("Input device added: {Path} ({Role})", device.Path, device.Role);
        return scan.Pending;
    }

    private void EventLoop(int generation, ScanState scans, int rescanMs)
    {
        var version = Volatile.Read(ref _devicesVersion);
        var devices = Devices();
        var polls = devices.Select(d => new PollFd { Fd = d.Fd, Events = NativeMethods.POLLIN }).ToArray();
        var nextScan = Environment.TickCount64 + rescanMs;

        while (generation == Volatile.Read(ref _generation))
        {
            if (_rescanDue || Environment.TickCount64 >= nextScan)
            {
                _rescanDue = false;
                var pending = AddNewDevices(generation, scans);
                nextScan = Environment.TickCount64 + (pending ? PendingRescanMs : rescanMs);
            }

            // the set changed (a device came or went): poll the new one
            if (version != Volatile.Read(ref _devicesVersion))
            {
                version = Volatile.Read(ref _devicesVersion);
                devices = Devices();
                polls = [.. devices.Select(d => new PollFd { Fd = d.Fd, Events = NativeMethods.POLLIN })];
            }
            if (polls.Length == 0)
            {
                Thread.Sleep(100);
                continue;
            }

            var ready = NativeMethods.Poll(ref polls[0], (nuint)polls.Length, 100);
            if (ready < 0) Thread.Sleep(10);   // EINTR, or a failure that would otherwise spin
            if (ready <= 0) continue;

            for (var i = 0; i < polls.Length; i++)
            {
                var device = devices[i];   // polls is built from devices, so the indices agree
                var revents = polls[i].Revents;
                if ((revents & (NativeMethods.POLLERR | NativeMethods.POLLHUP | NativeMethods.POLLNVAL)) != 0)
                {
                    DropDevice(device, closeFd: (revents & NativeMethods.POLLNVAL) == 0);
                    continue;
                }
                if ((revents & NativeMethods.POLLIN) == 0) continue;

                while (true)
                {
                    // stopped while a callback held this thread: these fd numbers may be a newer tap's by now
                    if (generation != Volatile.Read(ref _generation)) return;

                    var ev = new InputEvent();
                    var r = EvdevNativeMethods.Read(device.Fd, ref ev, (nuint)System.Runtime.InteropServices.Marshal.SizeOf<InputEvent>());
                    if (r <= 0) break;

                    // dispatch on the event, not the device: a combo device sends keys and motion down one fd
                    if (ev.Type == EvdevNativeMethods.EV_KEY)
                    {
                        HandleKeyCode(device, ev.Code, ev.Value);
                        if (ev.Value == 0 && _keyboardGrabPending) TryCompleteKeyboardGrab();
                    }
                    else if (device.Pointer)
                        HandleMouseEvent(ev, device);
                }
            }
        }
    }

    private void HandleKeyCode(InputDevice device, ushort code, int value)
    {
        if (value == 1) device.Held.Add(code);
        else if (value == 0) device.Held.Remove(code);

        if (device.Keyboard) HandleKeyboardEvent(code, value);
        else HandleButtonCode(code, value);
    }

    private void HandleKeyboardEvent(ushort code, int value)
    {
        if (_keyResolver == null) return;

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

    private void HandleMouseEvent(InputEvent ev, InputDevice device)
    {
        switch (ev.Type)
        {
            case EvdevNativeMethods.EV_REL:
                {
                    switch (ev.Code)
                    {
                        case EvdevNativeMethods.REL_X:
                            device.PendingDx += ev.Value * device.Scale;
                            break;
                        case EvdevNativeMethods.REL_Y:
                            device.PendingDy += ev.Value * device.Scale;
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
                if (device.PendingDx != 0 || device.PendingDy != 0)
                {
                    _onMouseDelta?.Invoke(device.PendingDx, device.PendingDy);
                    device.PendingDx = 0;
                    device.PendingDy = 0;
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
    internal bool KeysHeld()
    {
        lock (_devicesLock) return _devices.Any(d => d.Keyboard && AnyBitSet(d.Fd, 1, EvdevNativeMethods.BTN_MISC));
    }

    // whether a mouse button is down on any pointer, from the kernel's state: it sees through our own grab
    internal bool ButtonsHeld()
    {
        lock (_devicesLock) return _devices.Any(d => d.Pointer && AnyBitSet(d.Fd, EvdevNativeMethods.BTN_LEFT, EvdevNativeMethods.BTN_EXTRA + 1));
    }

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
        lock (_devicesLock)
        {
            if (!_grabbed || !_keyboardGrabPending || KeysHeld()) return;
            _keyboardGrabPending = false;
            GrabDevices(_devices.Where(d => d.Keyboard), true);
        }
        if (log.IsEnabled(LogLevel.Debug)) log.LogDebug("Keys released: keyboard grab taken");
    }

    // callers hold _devicesLock
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

    // callers hold _devicesLock
    private void GrabDevices(IEnumerable<InputDevice> devices, bool grab)
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

    // what the scans of one tap remember between them: new nodes waiting for udev and when each was first seen, and
    // the nodes that are neither keyboard nor pointer, with the input device each was
    private sealed class ScanState
    {
        public Dictionary<string, long> PendingSince { get; } = [];
        public Dictionary<string, string> Ignored { get; } = [];

        // a node gone from the directory: whatever takes its name next is another device
        public void Forget(List<string> present)
        {
            foreach (var gone in PendingSince.Keys.Except(present).ToList()) PendingSince.Remove(gone);
            foreach (var gone in Ignored.Keys.Except(present).ToList()) Ignored.Remove(gone);
        }
    }

    // what one scan of /dev/input found: the devices it opened, how many event nodes there were and how many of those
    // could not be opened, and whether one is still waiting for udev
    private sealed record DiscoveryResult(List<InputDevice> Added, int Nodes, int Unopened, bool Pending);
}
