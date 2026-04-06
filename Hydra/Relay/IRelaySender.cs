namespace Hydra.Relay;

public interface IRelaySender
{
    bool IsConnected { get; }
    ValueTask Send(string[] targetHosts, byte[] payload);
    event Func<string[], Task>? PeersChanged;
    event Func<string, MessageKind, string, Task>? MessageReceived;
    event Func<Task>? Disconnected;
}

public class NullRelaySender : IRelaySender
{
    public bool IsConnected => false;
    public ValueTask Send(string[] targetHosts, byte[] payload) => ValueTask.CompletedTask;
#pragma warning disable CS0067
    public event Func<string[], Task>? PeersChanged;
    public event Func<string, MessageKind, string, Task>? MessageReceived;
    public event Func<Task>? Disconnected;
#pragma warning restore CS0067
}
