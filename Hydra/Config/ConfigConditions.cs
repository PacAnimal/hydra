using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Cathedral.Extensions;
using Hydra.Platform;

namespace Hydra.Config;

public class ConfigConditions
{
    public string? Ssid { get; init; }
    public int? ScreenCount { get; init; }
    public bool? IsPluggedIn { get; init; }
    // an address of ours, or a network one is on: "192.168.1.7" or "192.168.1.0/24"
    public string? Address { get; init; }
    public string? GatewayMac { get; init; }
    public bool? Wired { get; init; }

    internal bool IsEmpty => Ssid == null && ScreenCount == null && IsPluggedIn == null && !HasLinkConditions;

    internal bool HasLinkConditions => Address != null || GatewayMac != null || Wired != null;

    private IPNetwork? _addressNetwork;
    private PhysicalAddress? _gatewayPhysicalAddress;

    // throw on a malformed value; Validate calls them so a bad one is a config error
    internal IPNetwork? AddressNetwork => Address == null ? null : _addressNetwork ??= ParseAddress(Address);
    internal PhysicalAddress? GatewayPhysicalAddress => GatewayMac == null ? null : _gatewayPhysicalAddress ??= ParseGatewayMac(GatewayMac);

    // every condition set here holds in state (AND logic)
    internal bool Matches(ConditionState state, bool ignoreScreenCount = false)
    {
        if (Ssid != null && !state.ActiveSsids.Any(s => s.EqualsIgnoreCase(Ssid))) return false;
        if (!ignoreScreenCount && ScreenCount != null && state.ScreenCount != ScreenCount) return false;
        if (IsPluggedIn != null && state.IsPluggedIn != IsPluggedIn) return false;
        if (AddressNetwork is { } network && state.Links?.Addresses.Any(network.Contains) != true) return false;
        if (GatewayPhysicalAddress is { } mac && state.Links?.GatewayMacs?.Contains(mac) != true) return false;
        return Wired == null || state.Links?.Wired == Wired;
    }

    // a bare address is a network of one; a network with host bits set (192.168.1.7/24) is refused, as it could mean either.
    // IPv4 must be dotted quad: IPAddress.TryParse also takes "10" for 0.0.0.10
    private static IPNetwork ParseAddress(string text)
    {
        var trimmed = text.Trim();
        var slash = trimmed.IndexOf('/');
        var spelled = slash < 0 ? trimmed : trimmed[..slash];
        if (IPAddress.TryParse(spelled, out var address) && (address.AddressFamily != AddressFamily.InterNetwork || spelled.Count(c => c == '.') == 3))
        {
            if (slash < 0) return new IPNetwork(address, address.GetAddressBytes().Length * 8);
            if (IPNetwork.TryParse(trimmed, out var network) && network.BaseAddress.Equals(address)) return network;
        }
        throw new InvalidOperationException(
            $"address condition '{text}' must be an IP address (192.168.1.7) or a network (192.168.1.0/24, with no bits set past the prefix).");
    }

    private static PhysicalAddress ParseGatewayMac(string text) => NetworkLinks.ParseMac(text)
        ?? throw new InvalidOperationException($"gatewayMac condition '{text}' must be a MAC address such as a4:91:b1:0c:22:7e.");
}
