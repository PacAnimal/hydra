using Hydra.Platform;
using Hydra.Platform.Linux.Wayland;
using Hydra.Screen;

namespace Tests.Platform.Wayland;

// a slave's session whose Injection answers the test completes, one per call
internal sealed class FakeWaylandSession : IWaylandSession, IDisposable
{
    private readonly List<TaskCompletionSource<IInjectionDevice>> _answers = [];
    private readonly SemaphoreSlim _asked = new(0);

    public int Calls
    {
        get { lock (_answers) return _answers.Count; }
    }

    public Task<IInjectionDevice> Injection(CancellationToken cancel)
    {
        var pending = new TaskCompletionSource<IInjectionDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_answers) _answers.Add(pending);
        _asked.Release();
        return pending.Task;
    }

    // waits for the count'th call to Injection
    public async Task Asked(int count)
    {
        using var timeout = new CancellationTokenSource(Bound);
        while (Calls < count) await _asked.WaitAsync(timeout.Token);
    }

    public void Fail(int call) => Pending(call).SetException(new InvalidOperationException("the user cancelled the dialog"));

    public void Succeed(int call, IInjectionDevice device) => Pending(call).SetResult(device);

    public void RaiseSessionLost() => SessionLost?.Invoke();

    private TaskCompletionSource<IInjectionDevice> Pending(int call)
    {
        lock (_answers) return _answers[call - 1];
    }

    public Task<InputCapturePortal> InputCapture(CancellationToken cancel) => throw new NotSupportedException();
    public Task<EiContext> Receiver(CancellationToken cancel) => throw new NotSupportedException();
    public Task<PortalClipboard?> Clipboard(CancellationToken cancel) => Task.FromResult<PortalClipboard?>(null);
    public Task<GlobalShortcutsPortal?> Shortcuts(CancellationToken cancel) => Task.FromResult<GlobalShortcutsPortal?>(null);
    public Task<IReadOnlyList<DetectedScreen>> Screens(CancellationToken cancel) => Task.FromResult<IReadOnlyList<DetectedScreen>>([]);
    public Task<PortalZoneSet> Zones(CancellationToken cancel) => throw new NotSupportedException();

    public event Action? ScreensChanged { add { } remove { } }

    public event Action? SessionLost;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _asked.Dispose();
}

// an injection device recording what it is sent, in order, as "key 30 down", "button 272 up", "abs 10,20", "rel 1,2",
// "scroll 0,120" and "frame"; StartEmulating and StopEmulating as "start" and "stop". BindKeysym binds to bindKey,
// when given, recorded as "bind 0xe9". group is the effective layout group, settable as the compositor's modifiers
// event would; it cannot select a group, as EIS cannot
internal sealed class RecordingDevice(string name = "recording", string? keymap = null, IReadOnlyList<ScreenBounds>? regions = null, uint? locked = 0,
    uint? bindKey = null, uint group = 0) : IInjectionDevice
{
    private readonly List<string> _sent = [];

    public IReadOnlyList<string> Sent
    {
        get { lock (_sent) return [.. _sent]; }
    }

    public string Name { get; } = name;
    public string? Keymap { get; } = keymap;
    public IReadOnlyList<ScreenBounds>? AbsoluteRegions { get; } = regions;
    public uint? LockedModifiers { get; } = locked;
    public uint Group { get; set; } = group;
    public bool SelectsGroup => false;
    public bool IsLost { get; private set; }

    public event Action<uint>? LockedModifiersChanged { add { } remove { } }
    public event Action? Lost;

    public bool SelectGroup(uint? selected) => false;

    public void Lose()
    {
        IsLost = true;
        Lost?.Invoke();
    }

    public void StartEmulating() => Record("start");
    public void StopEmulating() => Record("stop");
    public bool Key(uint evdevKey, bool pressed) => Record($"key {evdevKey} {(pressed ? "down" : "up")}");

    public uint? BindKeysym(uint keysym)
    {
        if (bindKey == null) return null;
        Record($"bind 0x{keysym:x}");
        return bindKey;
    }
    public bool Button(uint evdevButton, bool pressed) => Record($"button {evdevButton} {(pressed ? "down" : "up")}");
    public bool MotionAbsolute(double x, double y) => Record($"abs {x},{y}");
    public bool Motion(double dx, double dy) => Record($"rel {dx},{dy}");
    public bool Scroll(int dx, int dy) => Record($"scroll {dx},{dy}");
    public void Frame() => Record("frame");

    private bool Record(string what)
    {
        lock (_sent) _sent.Add(what);
        return true;
    }

    public void Dispose()
    {
    }
}

// the wlroots virtual devices, recording what VirtualInjectionDevice sends as "keymap N" (the Nth keymap uploaded,
// from 0, kept in Keymaps), "key 30 down", "mods 1,0,0,0", "flush" and the pointer's requests
internal sealed class RecordingVirtualInput : IVirtualInputConnection
{
    private readonly List<string> _sent = [];
    private readonly List<string> _keymaps = [];

    public IReadOnlyList<string> Sent
    {
        get { lock (_sent) return [.. _sent]; }
    }

    public IReadOnlyList<string> Keymaps
    {
        get { lock (_sent) return [.. _keymaps]; }
    }

    public bool IsDisconnected { get; private set; }

    public event Action? Disconnected;

    public IReadOnlyList<DetectedScreen> Screens { get; set; } = [];

    public void Lose()
    {
        IsDisconnected = true;
        Disconnected?.Invoke();
    }

    public void UploadKeymap(string keymapText)
    {
        lock (_sent)
        {
            _keymaps.Add(keymapText);
            _sent.Add($"keymap {_keymaps.Count - 1}");
        }
    }

    public void Key(uint evdevKey, bool pressed) => Record($"key {evdevKey} {(pressed ? "down" : "up")}");
    public void Modifiers(uint depressed, uint latched, uint locked, uint group) => Record($"mods {depressed},{latched},{locked},{group}");
    public void Button(uint evdevButton, bool pressed) => Record($"button {evdevButton} {(pressed ? "down" : "up")}");
    public void Motion(double dx, double dy) => Record($"rel {dx},{dy}");
    public void MotionAbsolute(AbsoluteMotion motion) => Record($"abs {motion.X},{motion.Y}");
    public void AxisSource(uint source) => Record($"source {source}");
    public void AxisDiscrete(uint axis, double value, int discrete) => Record($"axis {axis} {discrete}");
    public void Frame() => Record("frame");
    public void Flush() => Record("flush");

    private void Record(string what)
    {
        lock (_sent) _sent.Add(what);
    }

    public void Dispose()
    {
    }
}