using System.Text;
using Hydra.Relay;

namespace Tests.Setup;

public sealed class FakeRelay : NullRelaySender
{
    public bool Connected { get; set; } = true;
    public override bool IsConnected => Connected;
    public override event Func<string[], Task>? PeersChanged;
    public override event Func<string, MessageKind, ReadOnlyMemory<byte>, Task>? MessageReceived;
    public override event Func<Task>? Disconnected;

    // everything sent through this relay
    public SentSignals Sent { get; } = new();

    public override void Send(string[] targetHosts, byte[] payload) => Sent.Record(targetHosts, payload);

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
