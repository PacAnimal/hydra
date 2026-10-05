namespace Hydra.Relay;

public interface IRelaySender
{
    bool IsConnected { get; }
    RelayTransportSnapshot? Transport { get; }
    void Send(string[] targetHosts, byte[] payload);
    bool RequestReconnect();
    ValueTask SuspendConnectionAsync(CancellationToken cancel = default);
    ValueTask SuspendForSystemSleepAsync(long generation, CancellationToken cancel = default);
    void ResumeConnection();
    void BeginSystemWake(long generation);
    void CompleteSystemWake(long generation);
    ValueTask SendReliableAsync(string[] targetHosts, byte[] payload, CancellationToken cancel = default);
    // The caller already has the dx/dy as ints; sending them through this instead of pre-encoding to
    // bytes lets an implementation that batches relative-mouse traffic (RelayConnection) accumulate on
    // the numeric values directly, with no decode of its own payload just to add two more deltas to it.
    void SendMouseDelta(string[] targetHosts, int dx, int dy);
    // The same bargain for keys: the caller already holds the KeyEventMessage, and an implementation that
    // bundles key traffic (RelayConnection) appends the typed event straight into the open frame instead of
    // decoding the payload it was just handed.
    void SendKeyEvent(string[] targetHosts, KeyEventMessage message);
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

// A relay that sends nothing and raises nothing. Every member is virtual, and the derived operations route
// through the primitive ones (a reliable send through Send, a sleep suspend through the plain suspend), so a
// subclass overrides only what it needs.
public class NullRelaySender : IRelaySender
{
    public virtual bool IsConnected => false;
    public virtual RelayTransportSnapshot? Transport => null;
    public virtual void Send(string[] targetHosts, byte[] payload) { }
    public virtual bool RequestReconnect() => false;
    public virtual ValueTask SuspendConnectionAsync(CancellationToken cancel = default) => ValueTask.CompletedTask;
    public virtual ValueTask SuspendForSystemSleepAsync(long generation, CancellationToken cancel = default) => SuspendConnectionAsync(cancel);
    public virtual void ResumeConnection() { }
    public virtual void BeginSystemWake(long generation) => ResumeConnection();
    public virtual void CompleteSystemWake(long generation) => ResumeConnection();
    public virtual ValueTask SendReliableAsync(string[] targetHosts, byte[] payload, CancellationToken cancel = default)
    {
        cancel.ThrowIfCancellationRequested();
        Send(targetHosts, payload);
        return ValueTask.CompletedTask;
    }
    public virtual void SendMouseDelta(string[] targetHosts, int dx, int dy) =>
        Send(targetHosts, MessageSerializer.Encode(MessageKind.MouseMoveDelta, new MouseMoveDeltaMessage(dx, dy)));
    public virtual void SendKeyEvent(string[] targetHosts, KeyEventMessage message) =>
        Send(targetHosts, MessageSerializer.Encode(MessageKind.KeyEvent, message));
    public virtual event Func<string[], Task>? PeersChanged { add { } remove { } }
    public virtual event Func<string, MessageKind, ReadOnlyMemory<byte>, Task>? MessageReceived { add { } remove { } }
    public virtual event Func<Task>? Disconnected { add { } remove { } }
}
