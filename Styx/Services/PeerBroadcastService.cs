using System.Threading.Channels;
using Cathedral.Utils;
using Common.Interfaces;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Styx.Services;

public interface IPeerBroadcaster
{
    void QueueBroadcast(Guid networkId);
}

public class PeerBroadcastService(IClientRegistry registry, IHubContext<StyxHub, IStyxClient> hubContext, ILogger<PeerBroadcastService> log)
    : SimpleHostedService(log), IPeerBroadcaster
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        AllowSynchronousContinuations = false,
        SingleReader = true,
    });

    public void QueueBroadcast(Guid networkId) => _channel.Writer.TryWrite(networkId);

    protected override async Task Execute(CancellationToken cancel)
    {
        while (await _channel.Reader.WaitToReadAsync(cancel))
        {
            while (_channel.Reader.TryRead(out var networkId))
            {
                // drain consecutive duplicates — only broadcast once for the last unique ID seen
                while (_channel.Reader.TryRead(out var next))
                {
                    if (next != networkId)
                    {
                        await BroadcastPeers(networkId);
                        networkId = next;
                    }
                }

                await BroadcastPeers(networkId);
            }
        }
    }

    private async Task BroadcastPeers(Guid networkId)
    {
        try
        {
            var clients = await registry.GetNetworkClients(networkId);
            var allHostNames = clients.Select(c => c.HostName).ToArray();
            foreach (var (connectionId, hostName) in clients)
            {
                var peers = allHostNames.Where(h => h != hostName).ToArray();
                await hubContext.Clients.Client(connectionId).Peers(peers);
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to broadcast peers for network {NetworkId}", networkId);
        }
    }
}
