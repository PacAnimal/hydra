using Hydra.Relay;

namespace Tests.Setup;

public sealed class FakeRelay : IRelaySender
{
    public readonly List<(string[] Targets, MessageKind Kind)> Sent = [];
    public bool IsConnected => true;
    public event Action<string[]>? PeersChanged;
    public event Action<string, MessageKind, string>? MessageReceived;

    public ValueTask Send(string[] targetHosts, byte[] payload)
    {
        var (kind, _) = MessageSerializer.Decode(payload);
        Sent.Add((targetHosts, kind));
        return ValueTask.CompletedTask;
    }

    public void FirePeersChanged(params string[] hosts) => PeersChanged?.Invoke(hosts);
    public void FireMessageReceived(string host, MessageKind kind, string json) => MessageReceived?.Invoke(host, kind, json);
}
