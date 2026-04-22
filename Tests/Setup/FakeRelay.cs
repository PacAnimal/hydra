using System.Text;
using Hydra.Relay;

namespace Tests.Setup;

public sealed class FakeRelay : IRelaySender
{
    public readonly List<(string[] Targets, MessageKind Kind, string Json)> Sent = [];
    public bool IsConnected { get; set; } = true;
    public event Func<string[], Task>? PeersChanged;
    public event Func<string, MessageKind, ReadOnlyMemory<byte>, Task>? MessageReceived;
    public event Func<Task>? Disconnected;

    public void Send(string[] targetHosts, byte[] payload)
    {
        var decoded = MessageSerializer.Decode(payload);
        Sent.Add((targetHosts, decoded.Kind, decoded.Json));
    }

    public async Task FirePeersChanged(params string[] hosts)
    {
        if (PeersChanged != null) await PeersChanged(hosts);
    }

    public async Task FireMessageReceived(string host, MessageKind kind, string json)
    {
        if (MessageReceived != null) await MessageReceived(host, kind, Encoding.UTF8.GetBytes(json));
    }

    public async Task FireDisconnected()
    {
        if (Disconnected != null) await Disconnected();
    }
}
