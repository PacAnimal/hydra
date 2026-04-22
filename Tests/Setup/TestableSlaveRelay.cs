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
    private readonly SlaveCursorHider _hider;
    private readonly bool _ownsHider;

    public TestableSlaveRelay(
        IWorldState? worldState = null,
        IClipboardSync? clipboard = null,
        SlaveCursorHider? hider = null)
        : this(
            worldState ?? new WorldState(),
            clipboard ?? new NullClipboardSync(),
            hider ?? DefaultHider(),
            hider == null)
    { }

    private TestableSlaveRelay(IWorldState worldState, IClipboardSync clipboard, SlaveCursorHider hider, bool ownsHider)
        : base(
            TransitionTestHelper.Profile("slave", new HydraConfig { Mode = Mode.Slave }),
            NullLogger<RelayConnection>.Instance,
            new NullPlatformOutput(),
            new FakeScreenDetector(),
            worldState,
            hider,
            new NullScreenSaverSync(),
            new NullScreensaverSuppressor(),
            clipboard,
            FileTransferService.Null(), new NullFileSelectionDetector(), new NullOsdNotification())
    {
        _hider = hider;
        _ownsHider = ownsHider;
    }

    public Task SimulateMasterConfig(string host) => OnReceive(host, MessageKind.MasterConfig, "{}");
    public Task SimulateMasterConfig(string host, string json) => OnReceive(host, MessageKind.MasterConfig, json);
    public Task SimulateReceive(string host, MessageKind kind, string json) => OnReceive(host, kind, json);
    public Task SimulateDisconnected() => OnDisconnected();

    public override void Dispose()
    {
        if (_ownsHider) _hider.Dispose();
        base.Dispose();
    }

    private static SlaveCursorHider DefaultHider() =>
        new(new FakeCursorVisibility(), NullLogger<SlaveCursorHider>.Instance);
}
