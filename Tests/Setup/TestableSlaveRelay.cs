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
public sealed class TestableSlaveRelay(
    IWorldState? worldState = null,
    IClipboardSync? clipboard = null,
    ICursorHider? cursorHider = null) : SlaveRelayConnection(
        TransitionTestHelper.Profile("slave", new HydraConfig { Mode = Mode.Slave }),
        NullLogger<RelayConnection>.Instance,
        new NullPlatformOutput(),
        new FakeScreenDetector(),
        worldState ?? new WorldState(),
        cursorHider ?? new FakeCursorVisibility(),
        new NullScreenSaverSync(),
        new NullScreensaverSuppressor(),
        clipboard ?? new NullClipboardSync(),
        FileTransferService.Null(), new NullFileSelectionDetector(), new NullOsdNotification())
{
    public Task SimulateMasterConfig(string host) => OnReceive(host, MessageKind.MasterConfig, "{}"u8.ToArray());
    public Task SimulateMasterConfig(string host, string json) => OnReceive(host, MessageKind.MasterConfig, Encoding.UTF8.GetBytes(json));
    public Task SimulateReceive(string host, MessageKind kind, string json) => OnReceive(host, kind, Encoding.UTF8.GetBytes(json));
    public Task SimulateDisconnected() => OnDisconnected();
}
