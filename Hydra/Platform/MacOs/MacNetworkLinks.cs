using System.Net;
using System.Net.NetworkInformation;
using static Hydra.Platform.MacOs.NativeMethods;

namespace Hydra.Platform.MacOs;

internal sealed class MacNetworkLinks(MacShieldProcess? shield) : INetworkLinkSource
{
    // SystemConfiguration types Wi-Fi as IEEE80211 and the Thunderbolt bridge as Bridge; virtual adapters are not listed.
    // macOS reports a port with no cable as down
    public bool HasWiredLink(IReadOnlyList<NetworkInterface> live)
    {
        var wired = WiredNames();
        return live.Any(n => n.OperationalStatus == OperationalStatus.Up && wired.Contains(n.Name));
    }

    // Local Network privacy hides the table from Hydra itself, so the shield reads it, as it reads the SSID
    public async ValueTask<IReadOnlyList<Neighbour>> Neighbours(IReadOnlyList<IPAddress> gateways, CancellationToken cancel) =>
        shield == null ? throw new InvalidOperationException("the shield is not running to read the ARP table")
            : await shield.Neighbours(gateways, cancel) ?? throw new InvalidOperationException("the shield could not read the ARP table");

    // the shield sends each gateway a datagram before every read
    public void Resolve(IReadOnlyList<IPAddress> gateways) { }

    private static HashSet<string> WiredNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var all = SCNetworkInterfaceCopyAll();
        if (all == nint.Zero) return names;
        try
        {
            for (nint i = 0; i < CFArrayGetCount(all); i++)
            {
                var iface = CFArrayGetValueAtIndex(all, i);
                if (NativeHelpers.CfStringToManaged(SCNetworkInterfaceGetInterfaceType(iface)) != kSCNetworkInterfaceTypeEthernet) continue;
                if (NativeHelpers.CfStringToManaged(SCNetworkInterfaceGetBSDName(iface)) is { } name) names.Add(name);
            }
        }
        finally
        {
            CFRelease(all);
        }
        return names;
    }
}
