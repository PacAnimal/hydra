namespace Hydra.Relay;

public interface IRelaySender
{
    bool IsConnected { get; }
    RelayTransportSnapshot? Transport => null;
    void Send(string[] targetHosts, byte[] payload);
    bool RequestReconnect() => false;
    ValueTask SendReliableAsync(string[] targetHosts, byte[] payload, CancellationToken cancel = default)
    {
        cancel.ThrowIfCancellationRequested();
        Send(targetHosts, payload);
        return ValueTask.CompletedTask;
    }
    event Func<string[], Task>? PeersChanged;
    event Func<string, MessageKind, ReadOnlyMemory<byte>, Task>? MessageReceived;
    event Func<Task>? Disconnected;
}

public sealed record RelayTransportSnapshot(
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

public class NullRelaySender : IRelaySender
{
    public bool IsConnected => false;
    public void Send(string[] targetHosts, byte[] payload) { }
#pragma warning disable CS0067
    public event Func<string[], Task>? PeersChanged;
    public event Func<string, MessageKind, ReadOnlyMemory<byte>, Task>? MessageReceived;
    public event Func<Task>? Disconnected;
#pragma warning restore CS0067
}
