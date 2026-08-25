using System.Net;
using System.Net.Sockets;

namespace Hydra.Relay;

internal static class RelaySocketConnector
{
    private static readonly TimeSpan AddressConnectTimeout = TimeSpan.FromSeconds(2);

    internal static async Task<Socket> ConnectAsync(DnsEndPoint target, CancellationToken cancellationToken)
    {
        var addresses = IPAddress.TryParse(target.Host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(target.Host, cancellationToken);
        if (addresses.Length > 1)
            addresses = [.. await RelayAddressPreference.OrderAsync(addresses, target.Port, cancellationToken)];
        return await ConnectAsync(addresses, target.Port, AddressConnectTimeout, cancellationToken);
    }

    internal static async Task<Socket> ConnectAsync(
        IEnumerable<IPAddress> addresses,
        int port,
        TimeSpan addressTimeout,
        CancellationToken cancellationToken)
    {
        var failures = new List<Exception>();
        foreach (var address in addresses.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(addressTimeout);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), attempt.Token);
                return socket;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
                failures.Add(ex);
            }
        }

        if (failures.Count == 0)
            throw new SocketException((int)SocketError.HostNotFound);
        throw new AggregateException("Could not connect to any address resolved for the relay.", failures);
    }
}
