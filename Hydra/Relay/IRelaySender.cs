namespace Hydra.Relay;

public interface IRelaySender
{
    bool IsConnected { get; }
    RelayTransportSnapshot? Transport { get; }
    void Send(string[] targetHosts, byte[] payload);
    bool RequestReconnect();
    ValueTask SuspendConnection(CancellationToken cancel = default);
    ValueTask SuspendForSystemSleep(long generation, CancellationToken cancel = default);
    void ResumeConnection();
    void BeginSystemWake(long generation);
    void CompleteSystemWake(long generation);
    ValueTask SendReliableAsync(string[] targetHosts, byte[] payload, CancellationToken cancel = default);
    // typed rather than encoded, so a batching relay adds to an open batch without decoding it
    void SendMouseDelta(string[] targetHosts, int dx, int dy);
    // typed for the same reason, so a bundling relay appends it to the open frame
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
    public virtual ValueTask SuspendConnection(CancellationToken cancel = default) => ValueTask.CompletedTask;
    public virtual ValueTask SuspendForSystemSleep(long generation, CancellationToken cancel = default) => SuspendConnection(cancel);
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
        this.Send(targetHosts, MessageKind.MouseMoveDelta, new MouseMoveDeltaMessage(dx, dy));
    public virtual void SendKeyEvent(string[] targetHosts, KeyEventMessage message) =>
        this.Send(targetHosts, MessageKind.KeyEvent, message);
    public virtual event Func<string[], Task>? PeersChanged { add { } remove { } }
    public virtual event Func<string, MessageKind, ReadOnlyMemory<byte>, Task>? MessageReceived { add { } remove { } }
    public virtual event Func<Task>? Disconnected { add { } remove { } }
}

public static class RelaySenderExt
{
    public static void Send<T>(this IRelaySender relay, string host, MessageKind kind, T message) => relay.Send([host], kind, message);

    public static void Send<T>(this IRelaySender relay, string[] hosts, MessageKind kind, T message) =>
        relay.Send(hosts, MessageSerializer.Encode(kind, message));
}
