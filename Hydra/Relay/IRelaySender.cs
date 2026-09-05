namespace Hydra.Relay;

public interface IRelaySender
{
    bool IsConnected { get; }
    void Send(string[] targetHosts, byte[] payload);
    bool RequestReconnect() => false;
    ValueTask SuspendConnectionAsync(CancellationToken cancel = default) => ValueTask.CompletedTask;
    void ResumeConnection() { }
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
