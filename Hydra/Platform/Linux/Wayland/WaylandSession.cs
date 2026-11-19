using System.Text.Json;
using Hydra.Config;
using Hydra.Management;
using Hydra.Screen;
using Microsoft.Extensions.Logging;
using Tmds.DBus.Protocol;

namespace Hydra.Platform.Linux.Wayland;

// The run's one portal session (IWaylandSession has the contract). The first caller starts it and every caller shares
// that start; the start itself runs on the session's own token, so one caller giving up does not cancel it for the
// others. The grant's restore token is kept per mode in ConfigDir.PortalTokens and replaced after every Start.
internal sealed class WaylandSession : IWaylandSession
{
    internal const string AppId = "org.cathedral.Hydra";
    private const string EiName = "Hydra";
    private const string InputCaptureInterface = "org.freedesktop.portal.InputCapture";

    private readonly Mode _mode;
    private readonly string _tokenPath;
    private readonly ILogger _log;
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _disposing = new();
    private readonly CancellationToken _stop;
    private readonly SharedStart<Live> _starting;
    private Live? _current;
    // whether this run already dropped a restore token that came back without the clipboard; starts never overlap
    private bool _clipboardRetried;
    private bool _disposed;

    public WaylandSession(IHydraProfile profile, HydraRuntimeInfo runtime, ILogger<WaylandSession> log)
        : this(profile.Mode, new ConfigDir(runtime.ConfigPath), log, TimeProvider.System) { }

    internal WaylandSession(Mode mode, ConfigDir dir, ILogger log, TimeProvider time)
    {
        _mode = mode;
        _tokenPath = dir.PortalTokens;
        _log = log;
        _stop = _disposing.Token;
        // a failed start (the user cancelled the dialog, say) is not tried again by every caller at once
        _starting = new SharedStart<Live>(() => Task.Run(() => Start(_stop), _stop), time);
    }

    public event Action? ScreensChanged;
    public event Action? SessionLost;

    public async Task<InputCapturePortal> InputCapture(CancellationToken cancel)
    {
        RequireMode(Mode.Master);
        return (await Master(cancel)).Capture;
    }

    public async Task<EiContext> Receiver(CancellationToken cancel)
    {
        RequireMode(Mode.Master);
        return (await Master(cancel)).Ei;
    }

    public async Task<IInjectionDevice> Injection(CancellationToken cancel)
    {
        RequireMode(Mode.Slave);
        var live = await Slave(cancel);
        await live.Ready().WaitAsync(cancel);
        return live.Device;
    }

    public async Task<PortalClipboard?> Clipboard(CancellationToken cancel)
    {
        var live = await Current(cancel);
        return live.Clipboard(_log);
    }

    public async Task<GlobalShortcutsPortal?> Shortcuts(CancellationToken cancel)
    {
        RequireMode(Mode.Master);
        var live = await Master(cancel);
        return await live.Shortcuts(_log, _stop).WaitAsync(cancel);
    }

    public async Task<IReadOnlyList<DetectedScreen>> Screens(CancellationToken cancel)
    {
        var live = await Current(cancel);
        if (live is SlaveLive slave)
        {
            await slave.Known().WaitAsync(cancel);
            return slave.Regions();
        }
        var master = (MasterLive)live;
        return master.Named(master.FreshZones ?? await ReadZones(master, cancel));
    }

    public async Task<PortalZoneSet> Zones(CancellationToken cancel)
    {
        RequireMode(Mode.Master);
        var live = await Master(cancel);
        return live.FreshZones ?? await ReadZones(live, cancel);
    }

    private void RequireMode(Mode mode)
    {
        if (_mode != mode) throw new InvalidOperationException($"The Wayland session of a {_mode} has no {mode} side");
    }

    private async Task<MasterLive> Master(CancellationToken cancel) => (MasterLive)await Current(cancel);

    private async Task<SlaveLive> Slave(CancellationToken cancel) => (SlaveLive)await Current(cancel);

    private async Task<Live> Current(CancellationToken cancel)
    {
        Task<Live> starting;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            starting = _starting.Get();
        }
        return await starting.WaitAsync(cancel);
    }

    private async Task<Live> Start(CancellationToken cancel)
    {
        var tokens = await LoadTokens(cancel);
        var justWritten = WaylandDesktop.EnsureDesktopEntry(AppId, _log);
        var bus = await PortalBus.Connect(AppId, justWritten, _log, cancel);
        Live? live = null;
        try
        {
            if (_mode == Mode.Master)
            {
                await RequireInputCapture(bus);
                var master = new MasterLive(bus, await StartCapture(bus, tokens.Master, cancel), OutputNames.Open(_log));
                live = master;
                await StartMaster(master, tokens, cancel);
            }
            else if (await ChooseInjection(bus) is { } wayland)
            {
                var slave = new VirtualSlaveLive(bus, wayland);
                live = slave;
                StartVirtualInput(slave, wayland);
            }
            else
            {
                var slave = new EisSlaveLive(bus, await StartRemote(bus, tokens.Slave, cancel), OutputNames.Open(_log));
                live = slave;
                await StartRemoteDesktop(slave, tokens, cancel);
            }

            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (live.IsLost) throw new PortalException("The portal session ended as it started");
                live.Published = true;
                _current = live;
            }
            // a fresh session may lay the screens out anew, and a detector whose last read failed waits for this
            ScreensChanged?.Invoke();
            return live;
        }
        catch (Exception e)
        {
            if (e is not OperationCanceledException && _log.IsEnabled(LogLevel.Warning))
                _log.LogWarning("Starting the Wayland portal session failed: {Error}", e.Message);
            if (live != null) await live.DisposeAsync(_log);
            else await bus.DisposeAsync();
            throw;
        }
    }

    private async Task RequireInputCapture(PortalBus bus)
    {
        if (await bus.Has(InputCaptureInterface)) return;
        const string why = "The desktop portal has no InputCapture interface, which a Wayland master needs: it takes "
            + "xdg-desktop-portal-gnome 46, KDE Plasma 6.1 or xdg-desktop-portal-hyprland 1.5 or newer";
        if (_log.IsEnabled(LogLevel.Error)) _log.LogError(why);
        throw new PortalException(why);
    }

    // InputCapture's session, started again once a run without the restore token when restoring it lost the clipboard
    private async Task<InputCapturePortal> StartCapture(PortalBus bus, string? token, CancellationToken cancel)
    {
        var capture = await InputCapturePortal.Start(bus, token, wantClipboard: true, _log, cancel);
        if (!RetryForClipboard(token, capture.ClipboardSupported, capture.ClipboardEnabled, "input capture")) return capture;
        await capture.DisposeAsync();
        return await InputCapturePortal.Start(bus, null, wantClipboard: true, _log, cancel);
    }

    // RemoteDesktop's session, retried as StartCapture's
    private async Task<RemoteDesktopPortal> StartRemote(PortalBus bus, string? token, CancellationToken cancel)
    {
        var remote = await RemoteDesktopPortal.Start(bus, token, wantClipboard: true, _log, cancel);
        if (!RetryForClipboard(token, remote.ClipboardSupported, remote.ClipboardEnabled, "remote desktop")) return remote;
        await remote.DisposeAsync();
        return await RemoteDesktopPortal.Start(bus, null, wantClipboard: true, _log, cancel);
    }

    private bool RetryForClipboard(string? token, bool supported, bool enabled, string what)
    {
        if (!ClipboardRestoreRetry.ShouldRetry(token != null, askedClipboard: true, supported, enabled, _clipboardRetried)) return false;
        _clipboardRetried = true;
        if (_log.IsEnabled(LogLevel.Information))
            _log.LogInformation("The restored {What} session came without the clipboard; starting a fresh one without the restore token", what);
        return true;
    }

    // InputCapture's session and its EIS receiver, with the outputs read alongside to name the zones
    private async Task StartMaster(MasterLive live, PortalTokens tokens, CancellationToken cancel)
    {
        var capture = live.Capture;
        capture.Closed += () => Lose(live, "the portal closed the input capture session");
        capture.ZonesChanged += () =>
        {
            // stale from now: the serial it carries no longer sets barriers
            live.ZonesStale();
            _ = ZonesChanged(live);
        };
        live.Names?.WhenChanged(() => ScreensChanged?.Invoke());
        await SaveTokens(tokens with { Master = capture.RestoreToken }, cancel);
        using var eis = await capture.ConnectToEis();
        var ei = live.Connect(EiContext.Receiver(eis, EiName, _log));
        // a disconnect the dispatch thread saw before the handler was attached
        ei.Disconnected += () => Lose(live, "the EIS connection dropped");
        if (ei.IsDisconnected) Lose(live, "the EIS connection dropped");
    }

    // RemoteDesktop's session and its EIS sender, with the outputs read alongside to name the regions
    private async Task StartRemoteDesktop(EisSlaveLive live, PortalTokens tokens, CancellationToken cancel)
    {
        live.Remote.Closed += () => Lose(live, "the portal closed the remote desktop session");
        live.Names?.WhenChanged(() => DevicesChanged(live));
        await SaveTokens(tokens with { Slave = live.Remote.RestoreToken }, cancel);
        using var eis = await live.Remote.ConnectToEis();
        var sender = EiContext.Sender(eis, EiName, _log);
        // the session's own handler first, so the session hears a loss first; before any device can make it ready
        live.Connect(sender, () => Lose(live, "the EIS connection dropped"));
        if (sender.IsDisconnected) Lose(live, "the EIS connection dropped");
        sender.DeviceAdded += _ => DevicesChanged(live);
        sender.DeviceRemoved += _ => DevicesChanged(live);
        sender.DeviceResumed += _ => DevicesChanged(live);
        sender.DevicePaused += _ => DevicesChanged(live);
        // devices the dispatch thread announced before the handlers were attached
        DevicesChanged(live);
    }

    // the backend a slave injects through, chosen at each start: null for RemoteDesktop's EIS, else a connected Wayland
    // client with the wlroots virtual input protocols. Throws when neither is there.
    private async Task<WaylandClient?> ChooseInjection(PortalBus bus)
    {
        var hasRemoteDesktop = await bus.Has(RemoteDesktopPortal.Interface);
        WaylandClient? client = null;
        try
        {
            var backend = InjectionBackends.Choose(hasRemoteDesktop, () =>
            {
                client = WaylandClient.Connect(_log);
                return VirtualInput.IsSupported(client);
            });
            if (backend == InjectionBackend.Eis) return null;
            if (backend == InjectionBackend.VirtualInput) return client;
        }
        catch (InvalidOperationException e)
        {
            if (_log.IsEnabled(LogLevel.Debug)) _log.LogDebug("Cannot reach the Wayland compositor for virtual input: {Error}", e.Message);
        }
        client?.Dispose();
        const string why = "Cannot inject input on this Wayland desktop: the portal has no RemoteDesktop and the compositor offers no "
            + "zwlr_virtual_pointer_manager_v1 and zwp_virtual_keyboard_manager_v1";
        if (_log.IsEnabled(LogLevel.Error)) _log.LogError(why);
        throw new PortalException(why);
    }

    // the wlroots path: the compositor's keymap uploaded, the outputs as the screens, and the Wayland connection as the session
    private void StartVirtualInput(VirtualSlaveLive live, WaylandClient client)
    {
        client.Disconnected += () => Lose(live, "the Wayland connection dropped");
        var outputs = live.Watch(new WaylandOutputs(client, _log));
        outputs.Changed += () => OutputsChanged(live);
        live.Attach(new VirtualInjectionDevice(client, outputs, CompositorKeymap(client)));
        if (client.IsDisconnected) Lose(live, "the Wayland connection dropped");
        OutputsChanged(live);
        if (_log.IsEnabled(LogLevel.Information))
            _log.LogInformation("The portal has no RemoteDesktop; injecting through the compositor's wlroots virtual pointer and keyboard");
    }

    // the keymap the compositor gives its seat's keyboard, the layout the user set there; the system's layout only
    // when the compositor sent none or it did not compile
    private XkbKeymap CompositorKeymap(WaylandClient client)
    {
        if (client.SeatKeymap() is { } text)
        {
            try
            {
                var keymap = XkbKeymap.FromText(text);
                if (_log.IsEnabled(LogLevel.Information)) _log.LogInformation("Keyboard layout: the compositor's keymap");
                return keymap;
            }
            catch (InvalidOperationException e)
            {
                if (_log.IsEnabled(LogLevel.Warning)) _log.LogWarning("The compositor's keymap did not compile; using the system layout: {Error}", e.Message);
            }
        }
        else if (_log.IsEnabled(LogLevel.Warning))
        {
            _log.LogWarning("The compositor sent no keymap for its seat; using the system layout");
        }
        return XkbKeymap.FromSystemLayout(_log);
    }

    private void OutputsChanged(VirtualSlaveLive live)
    {
        if (live.SetOutputs()) ScreensChanged?.Invoke();
    }

    private void Lose(Live live, string reason)
    {
        if (!live.MarkLost()) return;
        lock (_lock)
        {
            // a session lost before it was published fails its own start
            if (!live.Published || _current != live) return;
            _current = null;
            _starting.Forget();
        }
        if (_log.IsEnabled(LogLevel.Warning)) _log.LogWarning("Wayland portal session lost: {Reason}", reason);
        // off the bus's and libei's threads, which the teardown waits on
        _ = Task.Run(() => live.DisposeAsync(_log));
        SessionLost?.Invoke();
    }

    private async Task<PortalZoneSet> ReadZones(MasterLive live, CancellationToken cancel)
    {
        var set = await live.Capture.GetZones(cancel);
        if (live.SetZones(set)) ScreensChanged?.Invoke();
        return set;
    }

    private async Task ZonesChanged(MasterLive live)
    {
        try
        {
            await ReadZones(live, _stop);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (_log.IsEnabled(LogLevel.Warning)) _log.LogWarning("Reading the input capture zones failed: {Error}", e.Message);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private void DevicesChanged(EisSlaveLive live)
    {
        if (live.DevicesChanged()) ScreensChanged?.Invoke();
    }

    private async Task<PortalTokens> LoadTokens(CancellationToken cancel)
    {
        try
        {
            if (!File.Exists(_tokenPath)) return new PortalTokens(null, null);
            var json = await File.ReadAllTextAsync(_tokenPath, cancel);
            return JsonSerializer.Deserialize<PortalTokens>(json, ManagementJson.Options) ?? new PortalTokens(null, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            if (_log.IsEnabled(LogLevel.Warning)) _log.LogWarning(LogEvents.FilePaths, "Cannot read the portal restore tokens from {Path}: {Error}", _tokenPath, e.Message);
            return new PortalTokens(null, null);
        }
    }

    private async Task SaveTokens(PortalTokens tokens, CancellationToken cancel)
    {
        try
        {
            await PrivateFile.Write(_tokenPath, ManagementJson.Serialize(tokens), PrivateFile.OwnerOnly, cancel);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the session runs regardless; the next start just asks again
            if (_log.IsEnabled(LogLevel.Warning)) _log.LogWarning(LogEvents.FilePaths, "Cannot save the portal restore token to {Path}: {Error}", _tokenPath, e.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task<Live>? starting;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            starting = _starting.Take();
            _current = null;
        }
        await _disposing.CancelAsync();
        if (starting != null)
        {
            try
            {
                var live = await starting;
                live.MarkLost();
                await live.DisposeAsync(_log);
            }
            catch (Exception)
            {
                // a start that failed tore itself down
            }
        }
        _disposing.Dispose();
    }

    private sealed record PortalTokens(string? Master, string? Slave);

    // one started session: the bus and what the mode and backend started on it
    private abstract class Live(PortalBus bus)
    {
        private int _lost;

        protected Lock Gate { get; } = new();
        protected PortalBus Bus { get; } = bus;
        internal bool Published { get; set; }
        internal bool IsLost => Volatile.Read(ref _lost) != 0;

        // true the first time only
        internal bool MarkLost()
        {
            if (Interlocked.Exchange(ref _lost, 1) != 0) return false;
            OnLost();
            return true;
        }

        protected virtual void OnLost()
        {
        }

        // null where the session enabled no portal clipboard
        internal abstract PortalClipboard? Clipboard(ILogger log);

        internal abstract Task DisposeAsync(ILogger log);

        protected Task DisposeBus(ILogger log) => Quietly(log, "the session bus", () => Bus.DisposeAsync().AsTask());

        protected static async Task Quietly(ILogger log, string what, Func<Task> dispose)
        {
            try
            {
                await dispose();
            }
            catch (Exception e)
            {
                if (log.IsEnabled(LogLevel.Debug)) log.LogDebug("Closing {What} failed: {Error}", what, e.Message);
            }
        }

        protected static Task Quietly(ILogger log, string what, Action dispose) => Quietly(log, what, () =>
        {
            dispose();
            return Task.CompletedTask;
        });
    }

    // a portal session's clipboard, made on first ask
    private sealed class SharedClipboard(PortalBus bus, ObjectPath session, bool enabled)
    {
        private readonly Lock _lock = new();
        private PortalClipboard? _clipboard;

        internal PortalClipboard? Get(ILogger log)
        {
            if (!enabled) return null;
            lock (_lock) return _clipboard ??= new PortalClipboard(bus, session, log);
        }

        internal async Task DisposeAsync()
        {
            PortalClipboard? clipboard;
            lock (_lock) clipboard = _clipboard;
            if (clipboard != null) await clipboard.DisposeAsync();
        }
    }

    // a master's: the InputCapture session, its EIS receiver, the GlobalShortcuts session beside it, and the outputs
    // that name its zones
    private sealed class MasterLive(PortalBus bus, InputCapturePortal capture, OutputNames? names) : Live(bus)
    {
        private readonly SharedClipboard _clipboard = new(bus, capture.Session, capture.ClipboardEnabled);
        private EiContext? _ei;
        private Task<GlobalShortcutsPortal?>? _shortcuts;
        private PortalZoneSet? _zones;
        private bool _zonesStale;

        internal InputCapturePortal Capture { get; } = capture;
        internal OutputNames? Names { get; } = names;

        // connected before the session is published
        internal EiContext Ei => Volatile.Read(ref _ei) ?? throw new InvalidOperationException("The EIS receiver is not connected yet");

        internal EiContext Connect(EiContext ei)
        {
            Volatile.Write(ref _ei, ei);
            return ei;
        }

        // the last zones read, unless a ZonesChanged has outdated them since
        internal PortalZoneSet? FreshZones
        {
            get
            {
                lock (Gate) return _zonesStale ? null : _zones;
            }
        }

        internal void ZonesStale()
        {
            lock (Gate) _zonesStale = true;
        }

        // true when the zones differ from the last ones read
        internal bool SetZones(PortalZoneSet set)
        {
            lock (Gate)
            {
                var changed = _zones != null && !_zones.Zones.SequenceEqual(set.Zones);
                _zones = set;
                _zonesStale = false;
                return changed;
            }
        }

        // the zones as screens, each named after the output it covers
        internal List<DetectedScreen> Named(PortalZoneSet set) => WaylandOutputs.Name(
            [.. set.Zones.Select(z => new ScreenBounds(z.X, z.Y, (int)z.Width, (int)z.Height))], Names?.Screens ?? [], "zone");

        internal override PortalClipboard? Clipboard(ILogger log) => _clipboard.Get(log);

        // started on first ask and shared; a failure leaves the run without hotkeys at home, never without the session.
        // One the portal closes is forgotten, so the next ask starts a fresh one
        internal Task<GlobalShortcutsPortal?> Shortcuts(ILogger log, CancellationToken cancel)
        {
            lock (Gate) return _shortcuts ??= StartShortcuts(log, cancel);
        }

        private async Task<GlobalShortcutsPortal?> StartShortcuts(ILogger log, CancellationToken cancel)
        {
            try
            {
                var shortcuts = await GlobalShortcutsPortal.Start(Bus, log, cancel);
                if (shortcuts == null && log.IsEnabled(LogLevel.Information))
                    log.LogInformation("The desktop portal has no GlobalShortcuts; hotkeys work only while the input is captured");
                if (shortcuts != null) shortcuts.Closed += () => ForgetShortcuts(shortcuts);
                return shortcuts;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                if (log.IsEnabled(LogLevel.Warning)) log.LogWarning("Starting the global shortcuts session failed: {Error}", e.Message);
                return null;
            }
        }

        private void ForgetShortcuts(GlobalShortcutsPortal closed)
        {
            lock (Gate)
            {
                if (_shortcuts is { IsCompletedSuccessfully: true } done && done.Result == closed) _shortcuts = null;
            }
        }

        internal override async Task DisposeAsync(ILogger log)
        {
            await Quietly(log, "the portal clipboard", _clipboard.DisposeAsync);
            await Quietly(log, "the global shortcuts session", async () =>
            {
                Task<GlobalShortcutsPortal?>? starting;
                lock (Gate) starting = _shortcuts;
                if (starting != null && await starting is { } shortcuts) await shortcuts.DisposeAsync();
            });
            await Quietly(log, "the EIS context", () => Volatile.Read(ref _ei)?.Dispose());
            await Quietly(log, "the Wayland outputs", () => Names?.Dispose());
            await Quietly(log, "the input capture session", () => Capture.DisposeAsync().AsTask());
            await DisposeBus(log);
        }
    }

    // a slave's: an injection device, ready once it can type and point, and the screens it moves over
    private abstract class SlaveLive : Live
    {
        private readonly SlaveScreenState _state;

        protected SlaveLive(PortalBus bus) : base(bus) => _state = new SlaveScreenState(Gate, () => IsLost);

        // made before the session is published
        internal abstract IInjectionDevice Device { get; }

        protected override void OnLost() => _state.Lost();

        internal Task Ready() => _state.Ready();

        internal Task Known() => _state.Known();

        internal IReadOnlyList<DetectedScreen> Regions() => _state.Regions();

        protected bool Update(Func<(bool Ready, IReadOnlyList<DetectedScreen>? Regions)> read) => _state.Update(read);
    }

    // RemoteDesktop's session, its EIS sender and the outputs that name the absolute pointer's regions
    private sealed class EisSlaveLive(PortalBus bus, RemoteDesktopPortal remote, OutputNames? names) : SlaveLive(bus)
    {
        private readonly SharedClipboard _clipboard = new(bus, remote.Session, remote.ClipboardEnabled);
        private EiContext? _ei;
        private EiInjectionDevice? _device;

        internal RemoteDesktopPortal Remote { get; } = remote;
        internal OutputNames? Names { get; } = names;

        internal override IInjectionDevice Device =>
            Volatile.Read(ref _device) ?? throw new InvalidOperationException("The EIS sender is not connected yet");

        // the session's loss handler goes on the sender before the device's own
        internal void Connect(EiContext sender, Action lost)
        {
            _ei = sender;
            sender.Disconnected += lost;
            Volatile.Write(ref _device, new EiInjectionDevice(sender));
        }

        internal override PortalClipboard? Clipboard(ILogger log) => _clipboard.Get(log);

        // re-reads readiness and the absolute pointer's regions; true when the regions changed. Read under the gate: the
        // start thread and the dispatch thread both call this, and a stale read must not land after a newer one. Gate
        // before ei's lock; ei raises its events outside that lock
        internal bool DevicesChanged()
        {
            if (_ei is not { } ei) return false;
            return Update(() =>
            {
                var ready = ei.CanTypeAndPoint;
                // added is enough: the regions are laid out before the compositor resumes the device, if it ever does
                var regions = ei.AbsoluteRegions?
                    .Select(r => new ScreenBounds((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height))
                    .ToList();
                return (ready, regions == null ? null : WaylandOutputs.Name(regions, Names?.Screens ?? [], "region"));
            });
        }

        internal override async Task DisposeAsync(ILogger log)
        {
            await Quietly(log, "the portal clipboard", _clipboard.DisposeAsync);
            await Quietly(log, "the injection device", () => _device?.Dispose());
            await Quietly(log, "the EIS context", () => _ei?.Dispose());
            await Quietly(log, "the Wayland outputs", () => Names?.Dispose());
            await Quietly(log, "the remote desktop session", () => Remote.DisposeAsync().AsTask());
            await DisposeBus(log);
        }
    }

    // the wlroots path: the Wayland connection, the outputs read through it and the virtual devices on it
    private sealed class VirtualSlaveLive(PortalBus bus, WaylandClient client) : SlaveLive(bus)
    {
        private WaylandOutputs? _outputs;
        private VirtualInjectionDevice? _device;

        internal override IInjectionDevice Device =>
            Volatile.Read(ref _device) ?? throw new InvalidOperationException("The virtual input devices are not made yet");

        internal WaylandOutputs Watch(WaylandOutputs outputs)
        {
            _outputs = outputs;
            return outputs;
        }

        internal void Attach(VirtualInjectionDevice device) => Volatile.Write(ref _device, device);

        // no portal session enables one here
        internal override PortalClipboard? Clipboard(ILogger log) => null;

        // ready once the devices exist; true when the outputs differ from the last ones read. Read under the gate, as
        // the start thread and the Wayland thread both call this; gate before the outputs' lock, which they raise
        // Changed outside
        internal bool SetOutputs() =>
            _outputs is { } outputs && Volatile.Read(ref _device) != null && Update(() => (true, outputs.Screens));

        internal override async Task DisposeAsync(ILogger log)
        {
            await Quietly(log, "the injection device", () => _device?.Dispose());
            await Quietly(log, "the Wayland connection", () =>
            {
                _outputs?.Dispose();
                client.Dispose();
            });
            await DisposeBus(log);
        }
    }

    // a Wayland connection of its own reading the outputs, whose names the portal's zones and regions take; the session
    // runs on without it
    private sealed class OutputNames : IDisposable
    {
        private readonly WaylandClient _client;
        private readonly WaylandOutputs _outputs;

        private OutputNames(WaylandClient client, WaylandOutputs outputs)
        {
            _client = client;
            _outputs = outputs;
        }

        internal IReadOnlyList<DetectedScreen> Screens => _outputs.Screens;

        // null when the compositor cannot be reached, which leaves the screens unnamed
        internal static OutputNames? Open(ILogger log)
        {
            WaylandClient? client = null;
            try
            {
                client = WaylandClient.Connect(log);
                return new OutputNames(client, new WaylandOutputs(client, log));
            }
            catch (Exception e) when (e is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
            {
                client?.Dispose();
                if (log.IsEnabled(LogLevel.Debug)) log.LogDebug("Cannot read the Wayland outputs to name the screens: {Error}", e.Message);
                return null;
            }
        }

        internal void WhenChanged(Action changed) => _outputs.Changed += changed;

        public void Dispose()
        {
            _outputs.Dispose();
            _client.Dispose();
        }
    }
}
