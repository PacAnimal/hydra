using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using static Hydra.Platform.Linux.Wayland.LibeiNativeMethods;

namespace Hydra.Platform.Linux.Wayland;

[Flags]
internal enum EiCapabilities
{
    None = 0,
    Pointer = EI_DEVICE_CAP_POINTER,
    PointerAbsolute = EI_DEVICE_CAP_POINTER_ABSOLUTE,
    Keyboard = EI_DEVICE_CAP_KEYBOARD,
    Scroll = EI_DEVICE_CAP_SCROLL,
    Button = EI_DEVICE_CAP_BUTTON,
}

internal sealed record EiRegion(uint X, uint Y, uint Width, uint Height);

// an ei_device the context holds a reference to, from DEVICE_ADDED until DEVICE_REMOVED or Dispose.
// Its state changes only under EiContext's lock.
internal sealed class EiDevice
{
    internal EiDevice(nint handle, string name, EiCapabilities capabilities, string? keymap, IReadOnlyList<EiRegion> regions)
    {
        Handle = handle;
        Name = name;
        Capabilities = capabilities;
        Keymap = keymap;
        Regions = regions;
    }

    internal nint Handle { get; }
    internal string Name { get; }
    internal EiCapabilities Capabilities { get; }
    // the device's XKB keymap text, null when it has none or it is not XKB
    internal string? Keymap { get; }
    internal IReadOnlyList<EiRegion> Regions { get; }
    internal bool IsResumed { get; set; }
    internal bool Emulating { get; set; }
    // sent an event since the last frame
    internal bool Dirty { get; set; }

    internal bool Has(EiCapabilities capability) => (Capabilities & capability) == capability;

    public override string ToString() => $"{Name} ({Capabilities})";
}

// one libei connection, as receiver (InputCapture on the master) or sender (RemoteDesktop on the slave).
// libei is not thread-safe, so every ei_* call is made under _lock. A dispatch thread reads the socket,
// translates events under the lock, and raises them after releasing it, so a handler may call back in.
internal sealed class EiContext : IDisposable
{
    private const int PollTimeoutMs = 100;
    // real XKB keymaps are tens of KiB; anything near this is a broken size
    private const nuint MaxKeymapBytes = 16 * 1024 * 1024;
    private const EiCapabilities Bound =
        EiCapabilities.Pointer | EiCapabilities.PointerAbsolute | EiCapabilities.Keyboard | EiCapabilities.Button | EiCapabilities.Scroll;

    private readonly Lock _lock = new();
    private readonly ILogger _log;
    private readonly bool _sender;
    private readonly List<nint> _seats = [];
    private readonly List<EiDevice> _devices = [];
    // outstanding Ping()s by ei_ping id
    private readonly Dictionary<ulong, TaskCompletionSource> _pings = [];
    private readonly Thread _thread;
    private nint _ei;
    private volatile bool _stopping;
    private volatile bool _disconnected;
    private string? _keyboardKeymap;
    private bool _emulating;
    private uint _sequence;

    // both roles. A disconnect can come before anyone subscribes: attach Disconnected, then check IsDisconnected.
    public event Action? Disconnected;
    public event Action<EiDevice>? DeviceAdded;
    public event Action<EiDevice>? DeviceRemoved;
    public event Action<EiDevice>? DeviceResumed;
    public event Action<EiDevice>? DevicePaused;
    public event Action<uint, uint, uint, uint>? KeyboardModifiers;
    // KeyboardKeymap changed, with its new value
    public event Action<string?>? KeyboardKeymapChanged;

    // receiver only
    public event Action<uint, bool>? KeyboardKey;
    public event Action<double, double>? PointerMotion;
    public event Action<double, double>? PointerMotionAbsolute;
    public event Action<uint, bool>? Button;
    public event Action<double, double>? ScrollDelta;
    public event Action<int, int>? ScrollDiscrete;
    public event Action<bool, bool>? ScrollStop;
    public event Action? Frame;

    private EiContext(nint ei, bool sender, ILogger log)
    {
        _ei = ei;
        _sender = sender;
        _log = log;
        _thread = new Thread(DispatchLoop) { Name = "HydraEiDispatch", IsBackground = true };
        _thread.Start();
    }

    public static EiContext Receiver(SafeHandle eisFd, string name, ILogger log) => Create(false, eisFd, name, log);

    public static EiContext Sender(SafeHandle eisFd, string name, ILogger log) => Create(true, eisFd, name, log);

    private static EiContext Create(bool sender, SafeHandle eisFd, string name, ILogger log)
    {
        var ei = sender ? ei_new_sender(0) : ei_new_receiver(0);
        if (ei == 0) throw new InvalidOperationException("libei could not create a context");
        try
        {
            ei_configure_name(ei, name);
            // libei closes the fd it is given, so it gets a duplicate and the SafeHandle keeps its own
            var fd = DupFd(eisFd);
            // libei owns the fd from here, failure included (libei.h: "takes ownership"), so it is not closed here
            var rc = ei_setup_backend_fd(ei, fd);
            if (rc < 0) throw new Win32Exception(-rc, "ei_setup_backend_fd");
        }
        catch
        {
            ei_unref(ei);
            throw;
        }
        return new EiContext(ei, sender, log);
    }

    private static int DupFd(SafeHandle handle)
    {
        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            var fd = SystemdNativeMethods.dup((int)handle.DangerousGetHandle());
            if (fd < 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "dup EIS descriptor");
            return fd;
        }
        finally
        {
            if (added) handle.DangerousRelease();
        }
    }

    public EiDevice? Keyboard { get { lock (_lock) return Pick(EiCapabilities.Keyboard); } }
    public EiDevice? Pointer { get { lock (_lock) return Pick(EiCapabilities.Pointer); } }
    public EiDevice? AbsolutePointer { get { lock (_lock) return Pick(EiCapabilities.PointerAbsolute); } }
    public bool CanTypeAndPoint { get { lock (_lock) return EiDevices.CanTypeAndPoint(_devices); } }
    // the first absolute pointer's regions, resumed or not; null with none added
    public IReadOnlyList<EiRegion>? AbsoluteRegions { get { lock (_lock) return EiDevices.AbsoluteRegions(_devices); } }

    // true once the EIS connection is gone; Disconnected has been or is about to be raised
    public bool IsDisconnected => _disconnected;

    // the XKB keymap of the first keyboard device that has one, paused or not
    public string? KeyboardKeymap { get { lock (_lock) return _keyboardKeymap; } }

    // completes once EIS has processed everything sent before it; fails if the connection drops or the
    // context is disposed first
    public Task Ping()
    {
        lock (_lock)
        {
            if (_ei == 0 || _disconnected) return Task.FromException(new ObjectDisposedException(nameof(EiContext), "the EIS connection is gone"));
            var ping = ei_new_ping(_ei);
            if (ping == 0) return Task.FromException(new InvalidOperationException("libei could not create a ping"));
            var pong = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pings[ei_ping_get_id(ping)] = pong;
            ei_ping(ping);
            ei_ping_unref(ping);
            return pong.Task;
        }
    }

    // -- sender --

    // starts emulating on every resumed device; one resumed later joins in while emulation is on
    public bool StartEmulating()
    {
        lock (_lock)
        {
            if (!_sender || _ei == 0) return false;
            _emulating = true;
            var any = false;
            foreach (var device in _devices)
            {
                if (!device.IsResumed) continue;
                Start(device);
                any = true;
            }
            return any;
        }
    }

    public bool StopEmulating()
    {
        lock (_lock)
        {
            if (!_sender || _ei == 0) return false;
            _emulating = false;
            var any = false;
            foreach (var device in _devices)
            {
                if (!device.Emulating) continue;
                ei_device_stop_emulating(device.Handle);
                device.Emulating = false;
                device.Dirty = false;
                any = true;
            }
            return any;
        }
    }

    public bool SendPointerMotion(double dx, double dy) =>
        Emit(EiCapabilities.Pointer, d => ei_device_pointer_motion(d, dx, dy));

    public bool SendPointerMotionAbsolute(double x, double y) =>
        Emit(EiCapabilities.PointerAbsolute, d => ei_device_pointer_motion_absolute(d, x, y));

    public bool SendButton(uint evdevButton, bool pressed) =>
        Emit(EiCapabilities.Button, d => ei_device_button_button(d, evdevButton, pressed));

    public bool SendScrollDiscrete(int dx, int dy) =>
        Emit(EiCapabilities.Scroll, d => ei_device_scroll_discrete(d, dx, dy));

    public bool SendKeyboardKey(uint evdevKey, bool pressed) =>
        Emit(EiCapabilities.Keyboard, d => ei_device_keyboard_key(d, evdevKey, pressed));

    // closes the frame on every device that was sent an event since the last one
    public bool SendFrame()
    {
        lock (_lock)
        {
            if (!_sender || _ei == 0) return false;
            var now = ei_now(_ei);
            var any = false;
            foreach (var device in _devices)
            {
                if (!device.Dirty) continue;
                device.Dirty = false;
                if (!device.IsResumed) continue;
                ei_device_frame(device.Handle, now);
                any = true;
            }
            return any;
        }
    }

    private bool Emit(EiCapabilities capability, Action<nint> send)
    {
        lock (_lock)
        {
            if (!_sender || _ei == 0) return false;
            var device = Pick(capability);
            if (device == null) return false;
            // sending outside start/stop emulating is a client bug, so an event starts it
            if (!device.Emulating)
            {
                _emulating = true;
                Start(device);
            }
            send(device.Handle);
            device.Dirty = true;
            return true;
        }
    }

    private void Start(EiDevice device)
    {
        if (device.Emulating) return;
        ei_device_start_emulating(device.Handle, ++_sequence);
        device.Emulating = true;
    }

    private EiDevice? Pick(EiCapabilities capability) => EiDevices.Pick(_devices, capability);

    // -- dispatch --

    private void DispatchLoop()
    {
        var raised = new List<Action>();
        while (!_stopping)
        {
            int fd;
            lock (_lock)
            {
                if (_ei == 0) return;
                fd = ei_get_fd(_ei);
            }
            var poll = new PollFd { Fd = fd, Events = NativeMethods.POLLIN };
            if (NativeMethods.Poll(ref poll, 1, PollTimeoutMs) <= 0) continue;

            var disconnected = false;
            lock (_lock)
            {
                if (_ei == 0) return;
                ei_dispatch(_ei);
                nint ev;
                while ((ev = ei_get_event(_ei)) != 0)
                {
                    try
                    {
                        disconnected |= Translate(ev, raised);
                    }
                    catch (Exception e)
                    {
                        if (_log.IsEnabled(LogLevel.Warning)) _log.LogWarning(e, "Dropped a libei event that could not be translated");
                    }
                    finally
                    {
                        ei_event_unref(ev);
                    }
                }
                var keymap = FirstKeymap();
                if (keymap != _keyboardKeymap)
                {
                    _keyboardKeymap = keymap;
                    raised.Add(() => KeyboardKeymapChanged?.Invoke(keymap));
                }
            }

            foreach (var raise in raised)
            {
                try
                {
                    raise();
                }
                catch (Exception e)
                {
                    if (_log.IsEnabled(LogLevel.Error)) _log.LogError(e, "libei event handler failed");
                }
            }
            raised.Clear();
            if (disconnected) return;
        }
    }

    // under _lock; returns true for the disconnect, the last event a context gets
    private bool Translate(nint ev, List<Action> raised)
    {
        var type = ei_event_get_type(ev);
        switch (type)
        {
            case EI_EVENT_CONNECT:
                if (_log.IsEnabled(LogLevel.Information)) _log.LogInformation("Connected to EIS as {Role}", _sender ? "sender" : "receiver");
                return false;
            case EI_EVENT_DISCONNECT:
                if (_log.IsEnabled(LogLevel.Information)) _log.LogInformation("Disconnected from EIS");
                _disconnected = true;
                var orphans = TakePings();
                raised.Add(() => FailPings(orphans, "the EIS connection dropped"));
                raised.Add(() => Disconnected?.Invoke());
                return true;
            case EI_EVENT_PONG:
                var ping = ei_event_pong_get_ping(ev);
                if (ping != 0 && _pings.Remove(ei_ping_get_id(ping), out var pong)) raised.Add(() => pong.TrySetResult());
                return false;
            case EI_EVENT_SEAT_ADDED:
                SeatAdded(ei_event_get_seat(ev));
                return false;
            case EI_EVENT_SEAT_REMOVED:
                var seat = ei_event_get_seat(ev);
                if (_seats.Remove(seat)) ei_seat_unref(seat);
                return false;
            case EI_EVENT_DEVICE_ADDED:
                var added = Wrap(ei_event_get_device(ev));
                if (added != null) raised.Add(() => DeviceAdded?.Invoke(added));
                return false;
            case EI_EVENT_DEVICE_REMOVED:
                var removed = Find(ei_event_get_device(ev));
                if (removed == null) return false;
                _devices.Remove(removed);
                removed.IsResumed = false;
                removed.Emulating = false;
                ei_device_unref(removed.Handle);
                if (_log.IsEnabled(LogLevel.Debug)) _log.LogDebug("EIS device removed: {Device}", removed);
                raised.Add(() => DeviceRemoved?.Invoke(removed));
                return false;
            case EI_EVENT_DEVICE_PAUSED:
                var paused = Find(ei_event_get_device(ev));
                if (paused == null) return false;
                // a pause ends the emulation sequence; the next resume needs a new one
                paused.IsResumed = false;
                paused.Emulating = false;
                paused.Dirty = false;
                if (_log.IsEnabled(LogLevel.Debug)) _log.LogDebug("EIS device paused: {Device}", paused);
                raised.Add(() => DevicePaused?.Invoke(paused));
                return false;
            case EI_EVENT_DEVICE_RESUMED:
                var resumed = Find(ei_event_get_device(ev));
                if (resumed == null) return false;
                resumed.IsResumed = true;
                if (_sender && _emulating) Start(resumed);
                if (_log.IsEnabled(LogLevel.Debug)) _log.LogDebug("EIS device resumed: {Device}", resumed);
                raised.Add(() => DeviceResumed?.Invoke(resumed));
                return false;
            case EI_EVENT_KEYBOARD_MODIFIERS:
                var depressed = ei_event_keyboard_get_xkb_mods_depressed(ev);
                var latched = ei_event_keyboard_get_xkb_mods_latched(ev);
                var locked = ei_event_keyboard_get_xkb_mods_locked(ev);
                var group = ei_event_keyboard_get_xkb_group(ev);
                raised.Add(() => KeyboardModifiers?.Invoke(depressed, latched, locked, group));
                return false;
            case EI_EVENT_FRAME:
                raised.Add(() => Frame?.Invoke());
                return false;
            case EI_EVENT_DEVICE_STOP_EMULATING:
                // the compositor stops emulating on the receiver as it deactivates a capture: the only sign it has
                // really released it, since the portal answers Release before acting
                var stopped = Find(ei_event_get_device(ev));
                if (stopped != null && _log.IsEnabled(LogLevel.Debug)) _log.LogDebug("EIS emulation stopped: {Device}", stopped);
                return false;
            case EI_EVENT_POINTER_MOTION:
                var dx = ei_event_pointer_get_dx(ev);
                var dy = ei_event_pointer_get_dy(ev);
                raised.Add(() => PointerMotion?.Invoke(dx, dy));
                return false;
            case EI_EVENT_POINTER_MOTION_ABSOLUTE:
                var x = ei_event_pointer_get_absolute_x(ev);
                var y = ei_event_pointer_get_absolute_y(ev);
                raised.Add(() => PointerMotionAbsolute?.Invoke(x, y));
                return false;
            case EI_EVENT_BUTTON_BUTTON:
                var button = ei_event_button_get_button(ev);
                var buttonDown = ei_event_button_get_is_press(ev);
                raised.Add(() => Button?.Invoke(button, buttonDown));
                return false;
            case EI_EVENT_SCROLL_DELTA:
                var sx = ei_event_scroll_get_dx(ev);
                var sy = ei_event_scroll_get_dy(ev);
                raised.Add(() => ScrollDelta?.Invoke(sx, sy));
                return false;
            case EI_EVENT_SCROLL_DISCRETE:
                var discreteX = ei_event_scroll_get_discrete_dx(ev);
                var discreteY = ei_event_scroll_get_discrete_dy(ev);
                raised.Add(() => ScrollDiscrete?.Invoke(discreteX, discreteY));
                return false;
            // a cancel is a stop with no kinetic scroll after it; both end the axis
            case EI_EVENT_SCROLL_STOP:
            case EI_EVENT_SCROLL_CANCEL:
                var stopX = ei_event_scroll_get_stop_x(ev);
                var stopY = ei_event_scroll_get_stop_y(ev);
                raised.Add(() => ScrollStop?.Invoke(stopX, stopY));
                return false;
            case EI_EVENT_KEYBOARD_KEY:
                var key = ei_event_keyboard_get_key(ev);
                var keyDown = ei_event_keyboard_get_key_is_press(ev);
                raised.Add(() => KeyboardKey?.Invoke(key, keyDown));
                return false;
            // SYNC is answered by the unref; touch and text are not used
            default:
                return false;
        }
    }

    private void SeatAdded(nint seat)
    {
        if (seat == 0 || _seats.Contains(seat)) return;
        ei_seat_ref(seat);
        _seats.Add(seat);
        try
        {
            EiSeatBinder.Bind(seat, Bound);
        }
        catch (Exception e)
        {
            if (_log.IsEnabled(LogLevel.Error)) _log.LogError(e, "Cannot bind EIS seat capabilities; no devices will be offered");
        }
    }

    private EiDevice? Find(nint handle)
    {
        foreach (var device in _devices)
            if (device.Handle == handle) return device;
        return null;
    }

    private EiDevice? Wrap(nint handle)
    {
        if (handle == 0 || Find(handle) != null) return null;
        ei_device_ref(handle);
        var name = Marshal.PtrToStringUTF8(ei_device_get_name(handle)) ?? "";
        var capabilities = EiCapabilities.None;
        foreach (var capability in Enum.GetValues<EiCapabilities>())
            if (capability != EiCapabilities.None && ei_device_has_capability(handle, (int)capability)) capabilities |= capability;
        var keymap = capabilities.HasFlag(EiCapabilities.Keyboard) ? ReadKeymap(handle) : null;
        var device = new EiDevice(handle, name, capabilities, keymap, ReadRegions(handle));
        _devices.Add(device);
        if (_log.IsEnabled(LogLevel.Debug))
            _log.LogDebug("EIS device added: {Device}, {Regions} regions, keymap {Keymap}", device, device.Regions.Count, keymap != null);
        return device;
    }

    private static List<EiRegion> ReadRegions(nint device)
    {
        var regions = new List<EiRegion>();
        nint region;
        for (nuint i = 0; (region = ei_device_get_region(device, i)) != 0; i++)
        {
            regions.Add(new EiRegion(ei_region_get_x(region), ei_region_get_y(region),
                ei_region_get_width(region), ei_region_get_height(region)));
        }
        return regions;
    }

    // the first keyboard device's keymap, paused or not; under _lock
    private string? FirstKeymap()
    {
        foreach (var device in _devices)
            if (device.Keymap != null) return device.Keymap;
        return null;
    }

    // a bad fd or size gives null, never an exception
    private string? ReadKeymap(nint device)
    {
        var keymap = ei_device_keyboard_get_keymap(device);
        if (keymap == 0 || ei_keymap_get_type(keymap) != EI_KEYMAP_TYPE_XKB) return null;
        var size = ei_keymap_get_size(keymap);
        var fd = ei_keymap_get_fd(keymap);
        if (fd < 0 || size == 0 || size > MaxKeymapBytes)
        {
            if (_log.IsEnabled(LogLevel.Warning)) _log.LogWarning("Ignoring EIS keymap: fd {Fd}, {Size} bytes", fd, size);
            return null;
        }
        try
        {
            // the fd stays the keymap's; read it by offset so no shared file position moves
            using var handle = new SafeFileHandle(fd, ownsHandle: false);
            var buffer = new byte[(int)size];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = RandomAccess.Read(handle, buffer.AsSpan(read), read);
                if (n <= 0) break;
                read += n;
            }
            var length = Array.IndexOf(buffer, (byte)0, 0, read);
            return Encoding.UTF8.GetString(buffer, 0, length < 0 ? read : length);
        }
        catch (Exception e)
        {
            if (_log.IsEnabled(LogLevel.Warning)) _log.LogWarning(e, "Cannot read EIS keymap");
            return null;
        }
    }

    public void Dispose()
    {
        _stopping = true;
        if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(2));
        List<TaskCompletionSource> pings;
        lock (_lock)
        {
            pings = TakePings();
            if (_ei != 0)
            {
                foreach (var device in _devices) ei_device_unref(device.Handle);
                _devices.Clear();
                foreach (var seat in _seats) ei_seat_unref(seat);
                _seats.Clear();
                ei_unref(_ei);
                _ei = 0;
            }
        }
        FailPings(pings, "the libei context was disposed");
    }

    // under _lock
    private List<TaskCompletionSource> TakePings()
    {
        var pings = _pings.Values.ToList();
        _pings.Clear();
        return pings;
    }

    private static void FailPings(List<TaskCompletionSource> pings, string why)
    {
        foreach (var ping in pings) ping.TrySetException(new ObjectDisposedException(nameof(EiContext), why));
    }
}
