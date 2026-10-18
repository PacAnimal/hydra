using System.Threading.Channels;
using Cathedral.Extensions;
using Cathedral.Utils;

namespace Styx.Services;

public interface IPeerBroadcaster
{
    void QueueBroadcast(NetworkKey network);
    // queues a membership taken from a caller-supplied snapshot instead of the live registry. Peers key off
    // the hostname, so displacing a duplicate and registering its replacement yields an identical list and
    // reads as no change at all — the snapshot is how a reconnecting host is shown leaving before it arrives.
    void QueueBroadcast(NetworkKey network, IReadOnlyList<NetworkClient> clients);
    // queues a snapshot and completes when it has joined every peer's queue, which delivers in order. A displaced host's
    // leaving is awaited before the displacing client is told it is authenticated, or its first payload can overtake the
    // notice that the name changed hands (Styx.md §6.2) — and the recipient still trusts the old holder. Its arrival
    // follows as a live broadcast; the pair would otherwise be an identical membership list, which reads as no change.
    Task BroadcastAndWait(NetworkKey network, IReadOnlyList<NetworkClient> clients, CancellationToken cancel);
}

public class PeerBroadcastService(IClientRegistry registry, PeerWriter writer, [FromKeyedServices(typeof(PeerWriter))] TimeProvider clock,
    ILogger<PeerBroadcastService> log)
    : SimpleHostedService(log), IPeerBroadcaster
{
    private readonly Channel<PeerBroadcast> _channel = Channel.CreateUnbounded<PeerBroadcast>(new UnboundedChannelOptions
    {
        AllowSynchronousContinuations = false,
        SingleReader = true,
    });

    public void QueueBroadcast(NetworkKey network) => _channel.Writer.TryWrite(new PeerBroadcast(network, null, null));

    public void QueueBroadcast(NetworkKey network, IReadOnlyList<NetworkClient> clients) => _channel.Writer.TryWrite(new PeerBroadcast(network, clients, null));

    public async Task BroadcastAndWait(NetworkKey network, IReadOnlyList<NetworkClient> clients, CancellationToken cancel)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_channel.Writer.TryWrite(new PeerBroadcast(network, clients, done))) return;

        // A stuck broadcaster must not wedge authentication. Losing the ordering guarantee is bad; refusing
        // to answer a login at all is worse, so this degrades rather than hangs — loudly.
        var timeout = Task.Delay(BroadcastWait, cancel);
        if (await Task.WhenAny(done.Task, timeout) == timeout)
            log.LogWarning("Timed out waiting for a displacement broadcast on network {Network}", network);
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
    // A broadcast somebody is waiting on is never collapsed away, or that caller waits for a delivery that
    // now never happens.
    private static bool CanCollapse(PeerBroadcast current, PeerBroadcast next) =>
        current.Snapshot is null && next.Snapshot is null && current.Done is null && next.Done is null &&
        current.Network == next.Network;

    private async Task BroadcastPeers(PeerBroadcast broadcast)
    {
        var network = broadcast.Network;
        try
        {
            var clients = broadcast.Snapshot ?? await registry.GetNetworkClients(network);
            var allHostNames = clients.Select(c => c.HostName).OrderBy(h => h, StringComparer.Ordinal).ToArray();
            if (log.IsEnabled(LogLevel.Information))
                log.LogInformation("Network {Network} peers: {Peers}", network, allHostNames.Length > 0 ? string.Join(", ", allHostNames) : "<none>");
            var sends = clients.Select(async client =>
            {
                var peers = allHostNames.Where(h => !h.EqualsOrdinal(client.HostName)).ToArray();
                try
                {
                    await writer.Peers(client.ConnectionId, peers, collapsible: broadcast.Snapshot is null);
                }
                catch (Exception ex)
                {
                    // One unwritable connection must not cost everybody after it their notification. A peer
                    // that never learns a name changed hands goes on trusting whoever used to hold it.
                    log.LogWarning(ex, "Could not deliver a peer snapshot to {ConnectionId} on network {Network}",
                        client.ConnectionId, network);
                }
            }).ToArray();

            // every snapshot has its place in each peer's order already, so the next broadcast need not wait on delivery
            _ = WarnIfStillWaiting(sends, network);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to broadcast peers for network {Network}", network);
        }
        finally
        {
            // a caller waiting on a broadcast that failed must not wait forever
            broadcast.Done?.TrySetResult();
        }
    }

    private async Task WarnIfStillWaiting(Task[] sends, NetworkKey network)
    {
        try
        {
            await Task.WhenAll(sends).WaitAsync(PeerWriter.Deadline, clock);
        }
        catch (TimeoutException)
        {
            log.LogWarning("A peer snapshot on network {Network} is still waiting to be taken after {Seconds} s", network,
                Constants.PeerWriteDeadlineSeconds);
        }
    }

    private record PeerBroadcast(NetworkKey Network, IReadOnlyList<NetworkClient>? Snapshot, TaskCompletionSource? Done);
}
