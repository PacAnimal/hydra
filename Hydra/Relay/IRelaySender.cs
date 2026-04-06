namespace Hydra.Relay;

public interface IRelaySender
{
    bool IsConnected { get; }
    ValueTask Send(string[] targetHosts, byte[] payload);
    event Action<string[]>? PeersChanged;
    event Action<string, MessageKind, string>? MessageReceived;
    event Action? Disconnected;
}

public class NullRelaySender : IRelaySender
{
    public bool IsConnected => false;
    public ValueTask Send(string[] targetHosts, byte[] payload) => ValueTask.CompletedTask;
#pragma warning disable CS0067
    public event Action<string[]>? PeersChanged;
    public event Action<string, MessageKind, string>? MessageReceived;
    public event Action? Disconnected;
#pragma warning restore CS0067
}
