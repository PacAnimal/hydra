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
}

public class PeerBroadcastService(IClientRegistry registry, IHubContext<StyxHub, IStyxClient> hubContext, ILogger<PeerBroadcastService> log)
    : SimpleHostedService(log), IPeerBroadcaster
{
    private readonly Channel<PeerBroadcast> _channel = Channel.CreateUnbounded<PeerBroadcast>(new UnboundedChannelOptions
    {
        AllowSynchronousContinuations = false,
        SingleReader = true,
    });

    public void QueueBroadcast(Guid networkId) => _channel.Writer.TryWrite(new PeerBroadcast(networkId, null));

    public void QueueBroadcast(Guid networkId, IReadOnlyList<NetworkClient> clients) => _channel.Writer.TryWrite(new PeerBroadcast(networkId, clients));

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
                        await BroadcastPeers(broadcast);
                        broadcast = next;
                    }
                }

                await BroadcastPeers(broadcast);
            }
        }
    }

    // only live-registry broadcasts collapse into each other. A snapshot is one step of an ordered sequence
    // (a reconnecting host leaving, then arriving), so dropping it loses the event it exists to convey.
    private static bool CanCollapse(PeerBroadcast current, PeerBroadcast next) =>
        current.Snapshot is null && next.Snapshot is null && current.NetworkId == next.NetworkId;

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
                await hubContext.Clients.Client(connectionId).Peers(peers);
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to broadcast peers for network {NetworkId}", networkId);
        }
    }

    private record PeerBroadcast(Guid NetworkId, IReadOnlyList<NetworkClient>? Snapshot);
}
