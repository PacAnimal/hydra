using Hydra.Screen;

namespace Hydra.Platform.Linux.Wayland;

// injection through an EIS sender on the RemoteDesktop portal's connection, typing with the keymap of the keyboard
// device the compositor offers. The session owns the context and disposes it; Dispose here only lets go of it.
internal sealed class EiInjectionDevice : IInjectionDevice
{
    private readonly EiContext _ei;
    private readonly Lock _lock = new();
    private uint? _locked;
    private uint _group;
    private EiDevice? _regionsOf;
    private IReadOnlyList<ScreenBounds>? _regions;

    internal EiInjectionDevice(EiContext ei)
    {
        _ei = ei;
        _ei.Disconnected += OnDisconnected;
        _ei.KeyboardModifiers += OnKeyboardModifiers;
    }

    public string Name => "EIS";

    // the keyboard device's own keymap: a new device brings a new string, so a caller can tell a change by reference
    public string? Keymap => _ei.Keyboard?.Keymap;

    // the absolute pointer's regions, converted once per device
    public IReadOnlyList<ScreenBounds>? AbsoluteRegions
    {
        get
        {
            var absolute = _ei.AbsolutePointer;
            lock (_lock)
            {
                if (ReferenceEquals(absolute, _regionsOf)) return _regions;
                _regionsOf = absolute;
                _regions = absolute?.Regions.Select(r => new ScreenBounds((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height)).ToList();
                return _regions;
            }
        }
    }

    public uint? LockedModifiers
    {
        get
        {
            lock (_lock) return _locked;
        }
    }

    // the compositor's, as its last modifiers event gave it
    public uint Group
    {
        get
        {
            lock (_lock) return _group;
        }
    }

    // libei has no request that sets the group: the compositor keeps it
    public bool SelectsGroup => false;

    public bool IsLost => _ei.IsDisconnected;

    public event Action<uint>? LockedModifiersChanged;

    public event Action? Lost;

    public bool SelectGroup(uint? group) => false;

    public void StartEmulating() => _ei.StartEmulating();

    public void StopEmulating() => _ei.StopEmulating();

    public bool Key(uint evdevKey, bool pressed) => _ei.SendKeyboardKey(evdevKey, pressed);

    // the keymap is the compositor's, which an EIS client cannot change
    public uint? BindKeysym(uint keysym) => null;

    public bool Button(uint evdevButton, bool pressed) => _ei.SendButton(evdevButton, pressed);

    public bool MotionAbsolute(double x, double y) => _ei.SendPointerMotionAbsolute(x, y);

    public bool Motion(double dx, double dy) => _ei.SendPointerMotion(dx, dy);

    public bool Scroll(int dx, int dy) => _ei.SendScrollDiscrete(dx, dy);

    public void Frame() => _ei.SendFrame();

    private void OnDisconnected() => Lost?.Invoke();

    private void OnKeyboardModifiers(uint depressed, uint latched, uint locked, uint group)
    {
        lock (_lock)
        {
            _locked = locked;
            _group = group;
        }
        LockedModifiersChanged?.Invoke(locked);
    }

    public void Dispose()
    {
        _ei.Disconnected -= OnDisconnected;
        _ei.KeyboardModifiers -= OnKeyboardModifiers;
    }
}
