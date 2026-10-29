using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Hydra.Platform;

// what each platform adds to .NET's interface list
internal interface INetworkLinkSource
{
    // a physical wired interface has a link: never Wi-Fi, a bridge, a VPN or a virtual machine's adapter. live is .NET's
    // list of interfaces that are up or unknown
    bool HasWiredLink(IReadOnlyList<NetworkInterface> live);

    // the IPv4 neighbour table's resolved entries, read for these gateways; throws when it cannot be read
    ValueTask<IReadOnlyList<Neighbour>> Neighbours(IReadOnlyList<IPAddress> gateways, CancellationToken cancel);

    // has the OS resolve gateways missing from the table
    void Resolve(IReadOnlyList<IPAddress> gateways);
}

internal sealed record Neighbour(IPAddress Address, PhysicalAddress Mac);

internal static class NetworkLinkReader
{
    private const int DiscardPort = 9;

    // how long a gateway missing from the neighbour table gets to answer the ARP a datagram to it starts
    private static readonly TimeSpan ResolveWait = TimeSpan.FromMilliseconds(500);

    // gatewayMacs false leaves the neighbour table alone, so nothing is sent: on macOS reading it asks for Local Network access
    internal static async Task<NetworkLinks> Read(INetworkLinkSource source, bool gatewayMacs, CancellationToken cancel)
    {
        // a VPN tunnel's operstate is often unknown, never up, and its addresses count all the same
        var live = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus is OperationalStatus.Up or OperationalStatus.Unknown && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .ToList();
        var addresses = live.SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address).Distinct().ToList();
        return new NetworkLinks(addresses, gatewayMacs ? await GatewayMacs(source, live, cancel) : null, source.HasWiredLink(live));
    }

    // null while a gateway stays unresolved, which is unknown rather than another network
    private static async Task<IReadOnlyList<PhysicalAddress>?> GatewayMacs(INetworkLinkSource source, List<NetworkInterface> live, CancellationToken cancel)
    {
        // a tunnel has no hardware address, so no neighbour entry will ever name its gateway
        var gateways = live.Where(n => n.GetPhysicalAddress().GetAddressBytes().Length == 6)
            .SelectMany(n => n.GetIPProperties().GatewayAddresses)
            .Select(g => g.Address)
            .Where(g => g.AddressFamily == AddressFamily.InterNetwork && !g.Equals(IPAddress.Any))
            .Distinct()
            .ToList();

        var neighbours = await source.Neighbours(gateways, cancel);
        if (Unresolved(gateways, neighbours) is { Count: > 0 } unresolved)
        {
            source.Resolve(unresolved);
            await Task.Delay(ResolveWait, cancel);
            neighbours = await source.Neighbours(gateways, cancel);
            if (Unresolved(gateways, neighbours).Count > 0) return null;
        }
        return [.. neighbours.Where(n => gateways.Contains(n.Address)).Select(n => n.Mac).Distinct()];
    }

    private static List<IPAddress> Unresolved(List<IPAddress> gateways, IReadOnlyList<Neighbour> neighbours) =>
        [.. gateways.Where(g => !neighbours.Any(n => n.Address.Equals(g)))];

    // a datagram to the discard port makes the OS resolve the address; whether it arrives does not matter
    internal static void Poke(IPAddress address)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Send([0], new IPEndPoint(address, DiscardPort));
        }
        catch (SocketException)
        {
            // no route right now; the next check tries again
        }
    }
}
