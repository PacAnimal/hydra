using System.Threading.Channels;
using Cathedral.Extensions;
using Cathedral.Utils;
using Common.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace Styx.Services;

public interface IPeerBroadcaster
{
    void QueueBroadcast(Guid networkId);
    // queues a membership taken from a caller-supplied snapshot instead of the live registry. Peers key off
    // the hostname, so displacing a duplicate and registering its replacement yields an identical list and
    // reads as no change at all — the snapshot is how a reconnecting host is shown leaving before it arrives.
    void QueueBroadcast(Guid networkId, IReadOnlyList<NetworkClient> clients);
    // queues a snapshot and completes when it has actually been delivered. The displacement half has to be
    // awaited before the displacing client is told it is authenticated, or it can send its first payload
    // ahead of the notice that the name changed hands — and the recipient still trusts the old holder.
    Task BroadcastAndWait(Guid networkId, IReadOnlyList<NetworkClient> clients, CancellationToken cancel);
}

public class PeerBroadcastService(IClientRegistry registry, IHubContext<StyxHub, IStyxClient> hubContext, ILogger<PeerBroadcastService> log)
    : SimpleHostedService(log), IPeerBroadcaster
{
    private readonly Channel<PeerBroadcast> _channel = Channel.CreateUnbounded<PeerBroadcast>(new UnboundedChannelOptions
    {
        AllowSynchronousContinuations = false,
        SingleReader = true,
    });

    public void QueueBroadcast(Guid networkId) => _channel.Writer.TryWrite(new PeerBroadcast(networkId, null, null));

    public void QueueBroadcast(Guid networkId, IReadOnlyList<NetworkClient> clients) => _channel.Writer.TryWrite(new PeerBroadcast(networkId, clients, null));

    public async Task BroadcastAndWait(Guid networkId, IReadOnlyList<NetworkClient> clients, CancellationToken cancel)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_channel.Writer.TryWrite(new PeerBroadcast(networkId, clients, done))) return;

        // A stuck broadcaster must not wedge authentication. Losing the ordering guarantee is bad; refusing
        // to answer a login at all is worse, so this degrades rather than hangs — loudly.
        var timeout = Task.Delay(BroadcastWait, cancel);
        if (await Task.WhenAny(done.Task, timeout) == timeout)
            log.LogWarning("Timed out waiting for a displacement broadcast on network {NetworkId}", networkId);
    }

    private static readonly TimeSpan BroadcastWait = TimeSpan.FromSeconds(5);

    protected override async Task Execute(CancellationToken cancel)
    {
        while (await _channel.Reader.WaitToReadAsync(cancel))
        {
            while (_channel.Reader.TryRead(out var broadcast))
            {
                // drain consecutive duplicates — only broadcast once for the last unique ID seen
                while (_channel.Reader.TryRead(out var next))
                {
                    if (!CanCollapse(broadcast, next))
                    {
                        await Deliver(broadcast);
                        broadcast = next;
                    }
                }

                await Deliver(broadcast);
            }
        }
    }

    // Whatever happens, whoever is waiting on this one is released — a caller blocked on a broadcast that
    // failed must not be blocked forever.
    private async Task Deliver(PeerBroadcast broadcast)
    {
        try
        {
            await BroadcastPeers(broadcast);
        }
        finally
        {
            broadcast.Done?.TrySetResult();
        }
    }

    // only live-registry broadcasts collapse into each other. A snapshot is one step of an ordered sequence
    // (a reconnecting host leaving, then arriving), so dropping it loses the event it exists to convey.
    // A broadcast somebody is waiting on is never collapsed away, or that caller waits for a delivery that
    // now never happens.
    private static bool CanCollapse(PeerBroadcast current, PeerBroadcast next) =>
        current.Snapshot is null && next.Snapshot is null && current.Done is null && next.Done is null &&
        current.NetworkId == next.NetworkId;

    private async Task BroadcastPeers(PeerBroadcast broadcast)
    {
        var networkId = broadcast.NetworkId;
        try
        {
            var clients = broadcast.Snapshot ?? await registry.GetNetworkClients(networkId);
            var allHostNames = clients.Select(c => c.HostName).OrderBy(h => h, StringComparer.Ordinal).ToArray();
            var peerList = allHostNames.Length > 0 ? string.Join(", ", allHostNames) : "<none>";
            log.LogInformation("Network {NetworkId} peers: {Peers}", networkId, peerList);
            foreach (var (connectionId, hostName) in clients)
            {
                var peers = allHostNames.Where(h => !h.EqualsOrdinal(hostName)).ToArray();
                try
                {
                    await hubContext.Clients.Client(connectionId).Peers(peers);
                }
                catch (Exception ex)
                {
                    // One unwritable connection must not cost everybody after it their notification. A peer
                    // that never learns a name changed hands goes on trusting whoever used to hold it.
                    log.LogWarning(ex, "Could not deliver a peer snapshot to {ConnectionId} on network {NetworkId}",
                        connectionId, networkId);
                }
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to broadcast peers for network {NetworkId}", networkId);
        }
    }

    private record PeerBroadcast(Guid NetworkId, IReadOnlyList<NetworkClient>? Snapshot, TaskCompletionSource? Done);
}
