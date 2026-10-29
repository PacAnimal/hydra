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

    // while held, a reliable send (a clipboard stream's Begin or chunk) is recorded, then its sender parks until released,
    // as behind a lane slow to drain: a test catches a stream mid-way at a known point
    public Gate Reliable { get; } = new();

    public override void Send(string[] targetHosts, byte[] payload)
    {
        Sent.Record(targetHosts, payload);
        Sending?.Invoke(MessageSerializer.Decode(payload).Kind, targetHosts);
    }

    // called on the sender's thread once a message is recorded, so a test acts at a known point in its sender's work
    public Action<MessageKind, string[]>? Sending { get; set; }

    public override async ValueTask SendReliableAsync(string[] targetHosts, byte[] payload, CancellationToken cancel = default)
    {
        cancel.ThrowIfCancellationRequested();
        Send(targetHosts, payload);
        await Reliable.Passed(cancel);
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
