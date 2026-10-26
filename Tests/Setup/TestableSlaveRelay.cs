using System.Text;
using Hydra.Config;
using Hydra.FileTransfer;
using Hydra.Platform;
using Hydra.Relay;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Setup;

/// <summary>
/// Consolidated testable subclass of SlaveRelayConnection.
/// All parameters are optional — pass only what the test needs to customise.
/// </summary>
public sealed class TestableSlaveRelay : SlaveRelayConnection
{
    public IActivityTracker Tracker { get; }
    public IDormancyState Dormancy { get; }
    public FakeScreenDetector Screens { get; }
    public NullPlatformOutput Output { get; }
    // everything the slave sent
    public SentSignals Sent { get; } = new();
    // while held, a reliable send (a clipboard stream's Begin or chunk) is recorded, then its sender parks until released
    public Gate Reliable { get; } = new();
    private readonly WebApplicationFactory<global::Styx.Program>? _styx;
    private readonly NotificationQueue<bool> _authenticated = new();

    // a slave that really connects, to the in-process relay the network config points at
    public TestableSlaveRelay(WebApplicationFactory<global::Styx.Program> styx, string networkConfig, IFileSelectionDetector selectionDetector)
        : this(MakeShared(null, null), null, null, null, new DormancyState(NullLogger<DormancyState>.Instance), new FakeScreenDetector(),
            new NullPlatformOutput(), selectionDetector, new NullOsdNotification(), networkConfig, log: TestLog.CreateLogger<RelayConnection>()) =>
        _styx = styx;

    public TestableSlaveRelay(
        IWorldState? worldState = null,
        IClipboardSync? clipboard = null,
        ICursorHider? cursorHider = null,
        IScreenSaverSync? screenSaverSync = null,
        Func<long>? trackerClock = null,
        IDormancyState? dormancy = null,
        FakeScreenDetector? screens = null,
        IFileSelectionDetector? selectionDetector = null,
        IOsdNotification? osd = null,
        FileTransferService? fileTransfer = null,
        ILogger<RelayConnection>? log = null,
        TimeProvider? clock = null,
        ClipboardSyncMode clipboardSync = ClipboardSyncMode.Hydra)
        : this(MakeShared(worldState, screenSaverSync, trackerClock), clipboard, cursorHider, screenSaverSync,
            dormancy ?? new DormancyState(NullLogger<DormancyState>.Instance), screens ?? new FakeScreenDetector(), new NullPlatformOutput(),
            selectionDetector ?? new NullFileSelectionDetector(), osd ?? new NullOsdNotification(), fileTransfer: fileTransfer, log: log, clock: clock,
            clipboardSync: clipboardSync)
    { }

    // ActivityTracker and SlaveRelayConnection must share the same WorldState — chaining lets us build it once
    private TestableSlaveRelay(
        SharedDeps deps,
        IClipboardSync? clipboard,
        ICursorHider? cursorHider,
        IScreenSaverSync? screenSaverSync,
        IDormancyState dormancy,
        FakeScreenDetector screens,
        NullPlatformOutput output,
        IFileSelectionDetector selectionDetector,
        IOsdNotification osd,
        string? networkConfig = null,
        FileTransferService? fileTransfer = null,
        ILogger<RelayConnection>? log = null,
        TimeProvider? clock = null,
        ClipboardSyncMode clipboardSync = ClipboardSyncMode.Hydra)
        : base(
            TransitionTestHelper.Profile("slave", new HydraConfig { Mode = Mode.Slave, NetworkConfig = networkConfig, ClipboardSync = clipboardSync }),
            log ?? NullLogger<RelayConnection>.Instance,
            output,
            screens,
            deps.WorldState,
            cursorHider ?? new FakeCursorVisibility(),
            screenSaverSync ?? new NullScreenSaverSync(),
            clipboard ?? new NullClipboardSync(),
            fileTransfer ?? FileTransferService.Null(), selectionDetector, osd,
            deps.Tracker, dormancy, clock)
    {
        Tracker = deps.Tracker;
        Dormancy = new DrainingDormancy(dormancy, ClipboardIdle);
        Screens = screens;
        Output = output;
    }

    public Task SimulateConnected() => OnAuthenticated();
    public Task SimulateMasterConfig(string host) => OnReceive(host, MessageKind.MasterConfig, "{}"u8.ToArray());
    public Task SimulateMasterConfig(string host, string json) => OnReceive(host, MessageKind.MasterConfig, Encoding.UTF8.GetBytes(json));
    // clipboard work runs on the slave's worker, so each waits for it to finish before returning; Deliver does not
    public async Task SimulateReceive(string host, MessageKind kind, string json)
    {
        await Deliver(host, kind, json);
        await ClipboardIdle();
    }

    public Task Deliver(string host, MessageKind kind, string json) => OnReceive(host, kind, Encoding.UTF8.GetBytes(json));

    public async Task SimulateDisconnected()
    {
        await OnDisconnected();
        await ClipboardIdle();
    }

    // completes once the clipboard worker has handled everything handed to it so far
    public Task ClipboardIdle() => DrainClipboardWork();

    // a departure drops streams on the clipboard worker
    protected override async Task OnPeers(string[] hostNames)
    {
        await base.OnPeers(hostNames);
        await ClipboardIdle();
    }

    // completes once the relay has logged the slave in again
    public Task NextAuthentication() => _authenticated.Next("the slave to authenticate");

    protected override TimeSpan ReconnectDelay => TimeSpan.Zero;

    protected override void ConfigureHubUrl(HttpConnectionOptions options)
    {
        if (_styx == null) base.ConfigureHubUrl(options);
        else options.UseTestServer(_styx.Server);
    }

    protected override async Task OnAuthenticated()
    {
        await base.OnAuthenticated();
        _authenticated.Push(true);
    }

    protected override void OnSent(string[] targetHosts, byte[] payload) => Sent.Record(targetHosts, payload);

    // unconnected, the relay's own would record the send and then throw, ending a clipboard stream at its Begin
    public override async ValueTask SendReliableAsync(string[] targetHosts, byte[] payload, CancellationToken cancel = default)
    {
        if (_styx != null)
        {
            await base.SendReliableAsync(targetHosts, payload, cancel);
            return;
        }
        cancel.ThrowIfCancellationRequested();
        OnSent(targetHosts, payload);
        await Reliable.Passed(cancel);
    }

    protected override void OnSentKey(string[] targetHosts, KeyEventMessage message) =>
        OnSent(targetHosts, MessageSerializer.Encode(MessageKind.KeyEvent, message));

    protected override void OnSentDelta(string[] targetHosts, int dx, int dy) =>
        OnSent(targetHosts, MessageSerializer.Encode(MessageKind.MouseMoveDelta, new MouseMoveDeltaMessage(dx, dy)));

    private static SharedDeps MakeShared(IWorldState? worldState, IScreenSaverSync? screenSaverSync, Func<long>? clock = null)
    {
        var ws = worldState ?? new WorldState();
        var tracker = new ActivityTracker(
            TransitionTestHelper.Profile("slave", new HydraConfig { Mode = Mode.Slave }),
            new Lazy<IRelaySender>(() => new NullRelaySender()),
            ws,
            screenSaverSync ?? new NullScreenSaverSync(),
            NullLogger<ActivityTracker>.Instance,
            clock);
        return new SharedDeps(ws, tracker);
    }

    private record SharedDeps(IWorldState WorldState, IActivityTracker Tracker);

    // going dormant drops streams on the clipboard worker, so Enter returns once it has
    private sealed class DrainingDormancy(IDormancyState inner, Func<Task> idle) : IDormancyState
    {
        public bool IsDormant => inner.IsDormant;

        public event Func<Task>? Entered
        {
            add => inner.Entered += value;
            remove => inner.Entered -= value;
        }

        public event Func<Task>? Exited
        {
            add => inner.Exited += value;
            remove => inner.Exited -= value;
        }

        public event Func<Task>? WakeDeadlineExpired
        {
            add => inner.WakeDeadlineExpired += value;
            remove => inner.WakeDeadlineExpired -= value;
        }

        public async Task Enter()
        {
            await inner.Enter();
            await idle();
        }

        public Task Exit() => inner.Exit();

        public bool RequestWake() => inner.RequestWake();
    }
}
