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

    private readonly SentSignals _signals = new();

    public override void Send(string[] targetHosts, byte[] payload) => _signals.Record(targetHosts, payload);

    public List<SentMessage> Snapshot() => _signals.Snapshot();
    public void ClearSent() => _signals.Clear();

    // completes once a message of this kind has been sent, for a sender working on its own thread
    public Task WaitForSent(MessageKind kind, TimeSpan timeout) => _signals.WaitFor(kind, timeout);

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
