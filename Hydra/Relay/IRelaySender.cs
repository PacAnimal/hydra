namespace Hydra.Relay;

public interface IRelaySender
{
    bool IsConnected { get; }
    void Send(string[] targetHosts, byte[] payload);
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
