using Hydra.Mouse;
using Hydra.Screen;

namespace Hydra.Platform.Linux.Wayland;

// injection through the wlroots virtual pointer and keyboard (Hyprland, Sway), for a compositor whose portal has no
// RemoteDesktop. The keyboard types on a keymap we upload, the compositor's own, and since Hyprland does not
// derive modifiers from a virtual keyboard's keys, every key that changes them is followed by the modifiers it left,
// as xkbcommon computes them. A keysym no key types is bound to a spare key on a keymap uploaded for it, as wtype
// does (BindKeysym), and the compositor's keymap goes back before any other key. Absolute moves are normalised over
// the bounding box of the compositor's outputs. The session owns the client and the outputs; the device owns the
// keymap, and Dispose destroys the virtual devices.
internal sealed class VirtualInjectionDevice : IInjectionDevice
{
    // wl_pointer axis units a wheel detent moves, as libinput and wlroots count it
    private const double AxisPerNotch = 15;

    private readonly IVirtualInputConnection _input;
    private readonly XkbKeymap _keymap;
    private readonly XkbKeymapLookup _lookup;
    private readonly XkbModifierState _modifiers;
    private readonly Lock _lock = new();
    private bool _pointerDirty;
    private IReadOnlyList<DetectedScreen>? _regionsOf;
    private IReadOnlyList<ScreenBounds>? _regions;
    // the keysym the spare key types on the keymap uploaded for it; null while the compositor's own is up
    private uint? _bound;
    private bool _spareHeld;
    private int _disposed;

    // takes the keymap over, disposed with the device
    internal VirtualInjectionDevice(WaylandClient client, WaylandOutputs outputs, XkbKeymap keymap)
        : this(keymap, () => new WaylandVirtualInput(client, outputs))
    {
    }

    // over the virtual devices open makes, for tests; takes the keymap over, disposed with the device
    internal VirtualInjectionDevice(XkbKeymap keymap, Func<IVirtualInputConnection> open)
    {
        _keymap = keymap;
        XkbKeymapLookup? lookup = null;
        XkbModifierState? modifiers = null;
        IVirtualInputConnection? input = null;
        try
        {
            Keymap = keymap.Text();
            lookup = new XkbKeymapLookup(keymap);
            modifiers = new XkbModifierState(keymap);
            input = open();
            input.UploadKeymap(Keymap);
        }
        catch
        {
            input?.Dispose();
            modifiers?.Dispose();
            lookup?.Dispose();
            keymap.Dispose();
            throw;
        }
        _lookup = lookup;
        _modifiers = modifiers;
        _input = input;
        _input.Disconnected += OnDisconnected;
    }

    public string Name => "wlroots virtual input";

    // the compositor's keymap, whatever is uploaded for a moment: the spare key types nothing on it
    public string? Keymap { get; }

    // the outputs' logical layout, converted once per layout
    public IReadOnlyList<ScreenBounds>? AbsoluteRegions
    {
        get
        {
            var screens = _input.Screens;
            lock (_lock)
            {
                if (ReferenceEquals(screens, _regionsOf)) return _regions;
                _regionsOf = screens;
                _regions = screens.Count == 0 ? null : [.. screens.Select(s => s.Bounds)];
                return _regions;
            }
        }
    }

    // ours to keep: the compositor takes the modifiers we send
    public uint? LockedModifiers => _modifiers.Current.Locked;

    public bool IsLost => _input.IsDisconnected;

    public event Action<uint>? LockedModifiersChanged;

    public event Action? Lost;

    // a virtual device is always on
    public void StartEmulating()
    {
    }

    // an idle device or an ended visit puts the compositor's keymap back
    public void StopEmulating() => Send(Restore);

    // the spare key, typing the keysym on a keymap uploaded for it unless it already does: only one keysym is bound
    // at a time, so another one while the spare key is held is refused rather than change what the held key types
    public uint? BindKeysym(uint keysym)
    {
        if (keysym == 0 || _lookup.SpareKey() is not { } spare) return null;
        uint? key = null;
        Send(() =>
        {
            if (_bound == keysym)
            {
                key = spare;
                return;
            }
            if (_spareHeld || KeymapBinding.Bind(Keymap!, spare, keysym) is not { } text) return;
            Upload(text);
            _bound = keysym;
            key = spare;
        });
        return key;
    }

    // the modifiers follow every key that changes them, since the compositor does not derive them itself
    public bool Key(uint evdevKey, bool pressed)
    {
        XkbModifiers? changed = null;
        uint lockedBefore = 0;
        var sent = Send(() =>
        {
            if (_bound != null && evdevKey == _lookup.SpareKey()) _spareHeld = pressed;
            else Restore();
            lockedBefore = _modifiers.Current.Locked;
            _input.Key(evdevKey, pressed);
            if (!_modifiers.Update(evdevKey, pressed)) return;
            var now = _modifiers.Current;
            _input.Modifiers(now.Depressed, now.Latched, now.Locked, now.Group);
            changed = now;
        });
        if (changed is { } mods && mods.Locked != lockedBefore) LockedModifiersChanged?.Invoke(mods.Locked);
        return sent;
    }

    // under _lock: the compositor's keymap back once the spare key is let go of
    private void Restore()
    {
        if (_bound == null || _spareHeld) return;
        Upload(Keymap!);
        _bound = null;
    }

    // under _lock. a new keymap resets the compositor's modifier state, so ours follows it: the keymaps differ only
    // in one key's symbols, so their modifier masks are the same
    private void Upload(string text)
    {
        _input.UploadKeymap(text);
        var now = _modifiers.Current;
        _input.Modifiers(now.Depressed, now.Latched, now.Locked, now.Group);
    }

    public bool Button(uint evdevButton, bool pressed) => SendPointer(() => _input.Button(evdevButton, pressed));

    public bool MotionAbsolute(double x, double y) =>
        VirtualInput.Normalise((int)Math.Round(x), (int)Math.Round(y), _input.Screens) is { } motion
        && SendPointer(() => _input.MotionAbsolute(motion));

    public bool Motion(double dx, double dy) => SendPointer(() => _input.Motion(dx, dy));

    // 120 a detent: whole detents are its discrete steps, a high-resolution wheel's fraction moves the axis alone
    public bool Scroll(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return false;
        return SendPointer(() =>
        {
            _input.AxisSource(VirtualInput.AxisSourceWheel);
            if (dy != 0) _input.AxisDiscrete(VirtualInput.AxisVertical, AxisValue(dy), dy / MouseScrollEvent.Notch);
            if (dx != 0) _input.AxisDiscrete(VirtualInput.AxisHorizontal, AxisValue(dx), dx / MouseScrollEvent.Notch);
        });
    }

    internal static double AxisValue(int units) => units * AxisPerNotch / MouseScrollEvent.Notch;

    // a pointer frame closes pointer requests; key requests need only reach the compositor
    public void Frame()
    {
        Send(() =>
        {
            if (_pointerDirty) _input.Frame();
            else _input.Flush();
            _pointerDirty = false;
        });
    }

    private bool SendPointer(Action send) => Send(() =>
    {
        send();
        _pointerDirty = true;
    });

    // false once the connection is gone: the session is tearing it down, and the handler hears it through Lost
    private bool Send(Action send)
    {
        lock (_lock)
        {
            if (_disposed != 0 || _input.IsDisconnected) return false;
            try
            {
                send();
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }

    private void OnDisconnected() => Lost?.Invoke();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _input.Disconnected -= OnDisconnected;
        lock (_lock)
        {
            if (!_input.IsDisconnected)
            {
                try
                {
                    _input.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // the client went first
                }
            }
            _modifiers.Dispose();
            _lookup.Dispose();
            _keymap.Dispose();
        }
    }
}

// what VirtualInjectionDevice drives: the wlroots virtual pointer and keyboard (VirtualInput), the Wayland connection
// they are on and the outputs' layout. Dispose destroys the virtual devices only
internal interface IVirtualInputConnection : IDisposable
{
    bool IsDisconnected { get; }

    // the connection is gone; raised once
    event Action? Disconnected;

    IReadOnlyList<DetectedScreen> Screens { get; }

    void UploadKeymap(string keymapText);

    void Key(uint evdevKey, bool pressed);

    void Modifiers(uint depressed, uint latched, uint locked, uint group);

    void Button(uint evdevButton, bool pressed);

    void Motion(double dx, double dy);

    void MotionAbsolute(AbsoluteMotion motion);

    void AxisSource(uint source);

    void AxisDiscrete(uint axis, double value, int discrete);

    // ends a group of pointer requests and sends them
    void Frame();

    // sends what was requested
    void Flush();
}

// the virtual devices on a live Wayland connection; the client and the outputs are the session's
internal sealed class WaylandVirtualInput(WaylandClient client, WaylandOutputs outputs) : IVirtualInputConnection
{
    private readonly VirtualInput _input = new(client);

    public bool IsDisconnected => client.IsDisconnected;

    public event Action? Disconnected
    {
        add => client.Disconnected += value;
        remove => client.Disconnected -= value;
    }

    public IReadOnlyList<DetectedScreen> Screens => outputs.Screens;

    public void UploadKeymap(string keymapText) => _input.UploadKeymap(keymapText);

    public void Key(uint evdevKey, bool pressed) => _input.Key(evdevKey, pressed);

    public void Modifiers(uint depressed, uint latched, uint locked, uint group) => _input.Modifiers(depressed, latched, locked, group);

    public void Button(uint evdevButton, bool pressed) => _input.Button(evdevButton, pressed);

    public void Motion(double dx, double dy) => _input.Motion(dx, dy);

    public void MotionAbsolute(AbsoluteMotion motion) => _input.MotionAbsolute(motion);

    public void AxisSource(uint source) => _input.AxisSource(source);

    public void AxisDiscrete(uint axis, double value, int discrete) => _input.AxisDiscrete(axis, value, discrete);

    public void Frame() => _input.Frame();

    public void Flush() => client.Flush();

    public void Dispose() => _input.Dispose();
}
