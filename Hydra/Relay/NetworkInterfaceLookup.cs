using Cathedral.Extensions;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Hydra.Relay;

internal static class NetworkInterfaceLookup
{
    /// <summary>
    /// The interface that owns a local address, comparing IPv4-mapped and plain forms alike. A scoped IPv6
    /// address goes by its scope id first. Null when nothing owns it, or when the OS will not enumerate.
    /// </summary>
    internal static NetworkInterface? FindByAddress(IPAddress address)
    {
        try
        {
            if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId > 0
                && FindByIndex((int)address.ScopeId, AddressFamily.InterNetworkV6) is { } scoped)
                return scoped;

            var normalized = Unmapped(address);
            return NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(network =>
                network.GetIPProperties().UnicastAddresses.Any(unicast => Unmapped(unicast.Address).Equals(normalized)));
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    internal static NetworkInterface? FindByIndex(int index, AddressFamily family) =>
        NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(network =>
        {
            try
            {
                var properties = network.GetIPProperties();
                return family == AddressFamily.InterNetwork
                    ? properties.GetIPv4Properties().Index == index
                    : properties.GetIPv6Properties().Index == index;
            }
            catch (NetworkInformationException)
            {
                // This interface doesn't support the queried address family (IPv6 disabled on an
                // Ethernet adapter, a Teredo/ISATAP tunnel with no IPv4 side, etc.) — skip just this
                // one interface rather than letting the exception abort the whole search.
                return false;
            }
        });

    /// <summary>
    /// A human name for the interface's kind. A VPN on macOS or Linux is a utun/tun/tap device the runtime
    /// reports as Unknown, so those go by name.
    /// </summary>
    internal static string Describe(NetworkInterface? network)
    {
        if (network == null) return "unknown";
        if (network.NetworkInterfaceType == NetworkInterfaceType.Unknown
            && (network.Name.StartsWithIgnoreCase("utun")
                || network.Name.StartsWithIgnoreCase("tun")
                || network.Name.StartsWithIgnoreCase("tap")))
            return "VPN / tunnel";
        return network.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Wireless80211 => "Wi-Fi",
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.Ethernet3Megabit
                or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT
                or NetworkInterfaceType.GigabitEthernet => "Ethernet",
            NetworkInterfaceType.Tunnel => "VPN / tunnel",
            NetworkInterfaceType.Loopback => "loopback",
            NetworkInterfaceType.Ppp => "PPP",
            _ => network.NetworkInterfaceType.ToString()
        };
    }

    private static IPAddress Unmapped(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
