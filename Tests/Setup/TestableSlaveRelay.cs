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
    private readonly SentSignals _signals = new();
    private readonly WebApplicationFactory<global::Styx.Program>? _styx;
    private readonly NotificationQueue<bool> _authenticated = new();

    // a slave that really connects, to the in-process relay the network config points at
    public TestableSlaveRelay(WebApplicationFactory<global::Styx.Program> styx, string networkConfig, IFileSelectionDetector selectionDetector)
        : this(MakeShared(null, null), null, null, null, new DormancyState(NullLogger<DormancyState>.Instance), new FakeScreenDetector(), new NullPlatformOutput(),
            selectionDetector, new NullOsdNotification(), networkConfig, log: TestLog.CreateLogger<RelayConnection>()) =>
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
        FileTransferService? fileTransfer = null)
        : this(MakeShared(worldState, screenSaverSync, trackerClock), clipboard, cursorHider, screenSaverSync,
            dormancy ?? new DormancyState(NullLogger<DormancyState>.Instance), screens ?? new FakeScreenDetector(), new NullPlatformOutput(),
            selectionDetector ?? new NullFileSelectionDetector(), osd ?? new NullOsdNotification(), fileTransfer: fileTransfer)
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
        ILogger<RelayConnection>? log = null)
        : base(
            TransitionTestHelper.Profile("slave", new HydraConfig { Mode = Mode.Slave, NetworkConfig = networkConfig }),
            log ?? NullLogger<RelayConnection>.Instance,
            output,
            screens,
            deps.WorldState,
            cursorHider ?? new FakeCursorVisibility(),
            screenSaverSync ?? new NullScreenSaverSync(),
            clipboard ?? new NullClipboardSync(),
            fileTransfer ?? FileTransferService.Null(), selectionDetector, osd,
            deps.Tracker, dormancy)
    {
        Tracker = deps.Tracker;
        Dormancy = dormancy;
        Screens = screens;
        Output = output;
    }

    public Task SimulateConnected() => OnAuthenticated();
    public Task SimulateMasterConfig(string host) => OnReceive(host, MessageKind.MasterConfig, "{}"u8.ToArray());
    public Task SimulateMasterConfig(string host, string json) => OnReceive(host, MessageKind.MasterConfig, Encoding.UTF8.GetBytes(json));
    public Task SimulateReceive(string host, MessageKind kind, string json) => OnReceive(host, kind, Encoding.UTF8.GetBytes(json));
    public Task SimulateDisconnected() => OnDisconnected();

    public List<SentMessage> Snapshot() => _signals.Snapshot();
    public void ClearSent() => _signals.Clear();
    public List<SentMessage> TakeAll() => _signals.TakeAll();

    // completes once a message of this kind has been sent, for a reply worked out off the receive path
    public Task WaitForSent(MessageKind kind, TimeSpan timeout) => _signals.WaitFor(kind, timeout);

    // completes once the relay has logged the slave in again
    public Task NextAuthentication(TimeSpan timeout) => _authenticated.Next((int)timeout.TotalMilliseconds, "the slave to authenticate");

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

    protected override void OnSent(string[] targetHosts, byte[] payload) => _signals.Record(targetHosts, payload);

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
}
