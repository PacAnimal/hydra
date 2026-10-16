using System.Collections.Concurrent;
using Common.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace Styx.Services;

/// <summary>
/// Every message the relay sends a peer, each with a deadline. A peer whose download has stalled while its pings still
/// arrive never trips the client timeout, so without one a write to it waits forever, and with it the sender's next
/// invocation and every membership broadcast. A peer that takes no message within the deadline is dropped: a cancelled
/// write leaves its bytes queued for it, so carrying on would only grow that queue.
/// </summary>
public sealed class PeerWriter(HubLifetimeManager<StyxHub> hub, TimeProvider clock, ILogger<PeerWriter> log)
{
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(Constants.PeerWriteDeadlineSeconds);

    private readonly ConcurrentDictionary<string, HubCallerContext> _connections = new();

    public void Track(HubCallerContext connection) => _connections[connection.ConnectionId] = connection;

    public void Forget(string connectionId) => _connections.TryRemove(connectionId, out _);

    public Task Receive(string connectionId, string sourceHost, string sourceIp, byte[] payload) =>
        Write(connectionId, nameof(IStyxClient.Receive), [sourceHost, sourceIp, payload]);

    public Task Peers(string connectionId, string[] hostNames) => Write(connectionId, nameof(IStyxClient.Peers), [hostNames]);

    public Task Kicked(string connectionId, string reason) => Write(connectionId, nameof(IStyxClient.Kicked), [reason]);

    private async Task Write(string connectionId, string method, object?[] args)
    {
        using var deadline = new CancellationTokenSource(Deadline, clock);
        try
        {
            await hub.SendConnectionAsync(connectionId, method, args, deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            if (!_connections.TryGetValue(connectionId, out var connection)) return;
            log.LogWarning("Dropping {ConnectionId}: it took no {Method} for {Seconds} s", connectionId, method, Constants.PeerWriteDeadlineSeconds);
            connection.Abort();
        }
    }
}
