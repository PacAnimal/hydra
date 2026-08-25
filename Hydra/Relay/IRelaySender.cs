namespace Hydra.Relay;

public interface IRelaySender
{
    bool IsConnected { get; }
    RelayTransportSnapshot? Transport => null;
    void Send(string[] targetHosts, byte[] payload);
    bool RequestReconnect() => false;
    ValueTask SuspendConnectionAsync(CancellationToken cancel = default) => ValueTask.CompletedTask;
    ValueTask SuspendForSystemSleepAsync(long generation, CancellationToken cancel = default) =>
        SuspendConnectionAsync(cancel);
    void ResumeConnection() { }
    void BeginSystemWake(long generation) => ResumeConnection();
    void CompleteSystemWake(long generation) => ResumeConnection();
    ValueTask SendReliableAsync(string[] targetHosts, byte[] payload, CancellationToken cancel = default)
    {
        cancel.ThrowIfCancellationRequested();
        Send(targetHosts, payload);
        return ValueTask.CompletedTask;
    }
    // The caller already has the dx/dy as ints; sending them through this instead of pre-encoding to
    // bytes lets an implementation that batches relative-mouse traffic (RelayConnection) accumulate on
    // the numeric values directly, with no decode of its own payload just to add two more deltas to it.
    void SendMouseDelta(string[] targetHosts, int dx, int dy) =>
        Send(targetHosts, MessageSerializer.Encode(MessageKind.MouseMoveDelta, new MouseMoveDeltaMessage(dx, dy)));
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
    long BytesReceived);

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
