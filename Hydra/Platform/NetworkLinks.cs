using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Hydra.Platform;

/// <summary>The local network as the address, gatewayMac and wired conditions see it.</summary>
/// <param name="Addresses">Every unicast address on an interface that is up.</param>
/// <param name="GatewayMacs">The hardware address of each IPv4 default gateway; null when not read, or a gateway did not resolve.</param>
/// <param name="Wired">A physical wired interface has a link: never Wi-Fi, a bridge, a VPN or a virtual machine's adapter.</param>
public sealed record NetworkLinks(IReadOnlyList<IPAddress> Addresses, IReadOnlyList<PhysicalAddress>? GatewayMacs, bool Wired)
{
    public static readonly NetworkLinks None = new([], [], false);

    // what a user copies into a condition; link-local addresses are on every interface and name no network
    public string Describe() =>
        $"{(Wired ? "wired" : "not wired")}, addresses {List(Addresses.Where(IsRoutable))}, gateway MAC {(GatewayMacs == null ? "unknown" : List(GatewayMacs.Select(FormatMac)))}";

    internal static bool IsRoutable(IPAddress address) =>
        !address.IsIPv6LinkLocal && !(address.AddressFamily == AddressFamily.InterNetwork && address.GetAddressBytes() is [169, 254, ..]);

    // lowercase and colon-separated, as ip and System Settings print it
    public static string FormatMac(PhysicalAddress mac) => string.Join(':', mac.GetAddressBytes().Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));

    // six octets split by ':' or '-', each one or two hex digits (macOS's arp drops leading zeros), or twelve digits unsplit
    public static PhysicalAddress? ParseMac(string text)
    {
        var trimmed = text.Trim();
        var octets = trimmed.Length == 12 ? [.. trimmed.Chunk(2).Select(c => new string(c))] : trimmed.Split(':', '-');
        if (octets.Length != 6) return null;
        var bytes = new byte[6];
        for (var i = 0; i < octets.Length; i++)
        {
            if (octets[i].Length is < 1 or > 2 || !byte.TryParse(octets[i], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out bytes[i]))
                return null;
        }
        return new PhysicalAddress(bytes);
    }

    private static string List(IEnumerable<object> items) => items.Select(i => i.ToString()).ToList() is { Count: > 0 } list ? string.Join(", ", list) : "none";
}
