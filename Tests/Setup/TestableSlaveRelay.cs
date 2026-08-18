using System.Text;
using Hydra.Config;
using Hydra.FileTransfer;
using Hydra.Platform;
using Hydra.Relay;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Setup;

/// <summary>
/// Consolidated testable subclass of SlaveRelayConnection.
/// All parameters are optional — pass only what the test needs to customise.
/// </summary>
public sealed class TestableSlaveRelay : SlaveRelayConnection
{
    public readonly List<(string[] Targets, MessageKind Kind, string Json)> Sent = [];
    public IActivityTracker Tracker { get; }
    public IDormancyState Dormancy { get; }
    public FakeScreenDetector Screens { get; }
    public NullPlatformOutput Output { get; }

    public TestableSlaveRelay(
        IWorldState? worldState = null,
        IClipboardSync? clipboard = null,
        ICursorHider? cursorHider = null,
        IScreenSaverSync? screenSaverSync = null,
        Func<long>? trackerClock = null,
        IDormancyState? dormancy = null,
        FakeScreenDetector? screens = null)
        : this(MakeShared(worldState, screenSaverSync, trackerClock), clipboard, cursorHider, screenSaverSync,
            dormancy ?? new DormancyState(NullLogger<DormancyState>.Instance), screens ?? new FakeScreenDetector(), new NullPlatformOutput())
    { }

    // ActivityTracker and SlaveRelayConnection must share the same WorldState — chaining lets us build it once
    private TestableSlaveRelay(
        SharedDeps deps,
        IClipboardSync? clipboard,
        ICursorHider? cursorHider,
        IScreenSaverSync? screenSaverSync,
        IDormancyState dormancy,
        FakeScreenDetector screens,
        NullPlatformOutput output)
        : base(
            TransitionTestHelper.Profile("slave", new HydraConfig { Mode = Mode.Slave }),
            NullLogger<RelayConnection>.Instance,
            output,
            screens,
            deps.WorldState,
            cursorHider ?? new FakeCursorVisibility(),
            screenSaverSync ?? new NullScreenSaverSync(),
            clipboard ?? new NullClipboardSync(),
            FileTransferService.Null(), new NullFileSelectionDetector(), new NullOsdNotification(),
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

    protected override void OnSent(string[] targetHosts, byte[] payload)
    {
        var decoded = MessageSerializer.Decode(payload);
        Sent.Add((targetHosts, decoded.Kind, decoded.Json));
    }

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
