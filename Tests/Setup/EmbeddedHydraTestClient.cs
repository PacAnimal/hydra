using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Hydra.Config;
using Hydra.Relay;
using Microsoft.Extensions.Logging;

namespace Tests.Setup;

/// <summary>
/// Like HydraTestClient but connects to a real TCP URL — used for embedded Styx server tests.
/// Does not override ConfigureHubUrl, so the base implementation (NoDelay socket) is used.
/// </summary>
public sealed class EmbeddedHydraTestClient(IHydraProfile profile, ILogger<RelayConnection>? log = null)
    : RelayConnection(profile, log ?? TestLog.CreateLogger<RelayConnection>(), new WorldState()), IAsyncDisposable
{
    private readonly SemaphoreSlim _readySignal = new(0);
    private readonly NotificationQueue<string[]> _peers = new();
    private readonly NotificationQueue<ReceivedMessage> _messages = new();
    private readonly NotificationQueue<string> _kicks = new();

    private readonly ConcurrentQueue<Socket> _connectorSockets = new();

    protected override TimeSpan ReconnectDelay => TimeSpan.Zero;

    protected override async Task<Socket> ConnectRelaySocket(DnsEndPoint target, CancellationToken cancel)
    {
        var socket = await base.ConnectRelaySocket(target, cancel);
        _connectorSockets.Enqueue(socket);
        return socket;
    }

    // closes every socket the relay's connector has opened so far
    public void CloseConnectorSockets()
    {
        while (_connectorSockets.TryDequeue(out var socket)) socket.Dispose();
    }

    protected override Task OnAuthenticated() { _readySignal.Release(); return Task.CompletedTask; }

    protected override Task OnReceive(string sourceHost, MessageKind kind, ReadOnlyMemory<byte> body)
    {
        _messages.Push(new ReceivedMessage(sourceHost, kind, Encoding.UTF8.GetString(body.Span)));
        return Task.CompletedTask;
    }

    protected override Task OnPeers(string[] hostNames)
    {
        _peers.Push(hostNames);
        return Task.CompletedTask;
    }

    protected override Task OnKicked(string reason)
    {
        _kicks.Push(reason);
        return Task.CompletedTask;
    }

    public async Task WaitForReady()
    {
        if (!await _readySignal.WaitAsync(TestBound.Bound))
            throw new TimeoutException("Timed out waiting for relay connection");
    }

    // each wait takes the next notification in arrival order, so two before a wait are never read as one
    public Task<string[]> WaitForPeers() => _peers.Next("peers update");

    public Task<ReceivedMessage> WaitForMessage() => _messages.Next("message");

    public Task<string> WaitForKick() => _kicks.Next("kick");

    public async ValueTask DisposeAsync()
    {
        using var cts = new CancellationTokenSource(Bound);
        try { await StopAsync(cts.Token); }
        catch { /* ignore stop errors in cleanup */ }
        Dispose();
    }
}
