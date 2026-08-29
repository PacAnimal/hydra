using Hydra.Config;

namespace Hydra.Management;

internal static class ManagementProtocol
{
    internal const int Version = 2;
    internal const int MaxMessageBytes = 2 * 1024 * 1024;
}

public sealed record ManagementRequest(string Method, string? Json = null);

public sealed record ManagementResponse(bool Success, string? Json = null, string? Error = null)
{
    public static ManagementResponse Ok<T>(T value) => new(true, ManagementJson.Serialize(value));
    public static ManagementResponse Empty() => new(true);
    public static ManagementResponse Fail(string error) => new(false, Error: error);
}

public sealed record ServerHello(int ProtocolVersion, string HydraVersion, string InstanceId, int ProcessId);

public sealed record ScreenStatus(string Name, string Host, int Width, int Height, decimal MouseScale, decimal? RelativeMouseScale);

public sealed record PeerStatus(string Name, string Platform, bool Connected, List<ScreenStatus> Screens);

public sealed record RouterStatus(bool IsRemote, string? ActiveHost, string? ActiveScreen, bool LockedToScreen, bool ConfinedToScreen, bool RelativeMouse);

public sealed record RelayConnectionStatus(
    string InterfaceName,
    string InterfaceType,
    string LocalAddress,
    int LocalPort,
    string RelayHost,
    string RemoteAddress,
    int RemotePort,
    DateTimeOffset ConnectedAt,
    long ConnectionAttempts,
    long MessagesSent,
    long MessagesReceived,
    long BytesSent,
    long BytesReceived,
    long SendQueueDepth,
    long MaxSendQueueDepth,
    long OldestQueuedMilliseconds,
    long LastSendLatencyMilliseconds);

public sealed record NetworkAdapterStatus(
    string Name,
    string Type,
    List<string> Addresses,
    bool HasGateway,
    long LinkSpeedBitsPerSecond,
    long? BytesReceived,
    long? BytesSent,
    long? ReceiveErrors,
    long? ReceiveDrops,
    long? SendErrors,
    long? SendDrops);
public sealed record EmbeddedRelayPeerStatus(string HostName, string RemoteAddress, string LocalAddress, string InterfaceName, string InterfaceType);
public sealed record PeerLatencyStatus(string Host, double LastRttMs, double AverageRttMs, double P95RttMs, double JitterMs, long Samples, long Lost, DateTimeOffset UpdatedAt);

public sealed record HydraStatusSnapshot(
    DateTimeOffset CapturedAt,
    string Version,
    int ProcessId,
    long UptimeSeconds,
    string ConfigPath,
    string ConfigRevision,
    string HostName,
    string? ProfileName,
    Mode Mode,
    bool IsIdle,
    bool RelayConnected,
    RelayConnectionStatus? RelayConnection,
    List<NetworkAdapterStatus> ActiveNetworkAdapters,
    List<EmbeddedRelayPeerStatus> EmbeddedRelayPeers,
    List<PeerLatencyStatus> PeerLatency,
    bool Dormant,
    List<ScreenStatus> LocalScreens,
    List<PeerStatus> Peers,
    RouterStatus? Router);

public sealed record ManagementLogEntry(long Cursor, DateTimeOffset Timestamp, string Level, string Category, string Message);
public sealed record ManagementLogPage(long LatestCursor, long Dropped, List<ManagementLogEntry> Entries);

public sealed record ConfigDocument(string Path, string Revision, string Json);
public sealed record ConfigValidation(bool Valid, string? Error = null);
public sealed record SaveConfigRequest(string ExpectedRevision, string Json, bool Restart);
public sealed record CommandResult(bool Accepted, string Message);

internal sealed record HydraRuntimeInfo(string ConfigPath, DateTimeOffset StartedAt);
