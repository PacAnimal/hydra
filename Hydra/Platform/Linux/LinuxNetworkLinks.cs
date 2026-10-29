using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;

namespace Hydra.Platform.Linux;

internal sealed class LinuxNetworkLinks : INetworkLinkSource
{
    private const string SysClassNet = "/sys/class/net";
    private const string ProcNetArp = "/proc/net/arp";

    // ARPHRD_ETHER in /sys/class/net/<if>/type; Wi-Fi reports it too
    private const string EtherType = "1";

    // ATF_COM: the entry's hardware address is resolved
    private const int ArpComplete = 0x2;

    // carrier, not an address: a NIC in a bridge holds none, its bridge does
    public bool HasWiredLink(IReadOnlyList<NetworkInterface> live) => live.Any(n => n.OperationalStatus == OperationalStatus.Up && IsWired(n));

    private static bool IsWired(NetworkInterface nic)
    {
        var link = new DirectoryInfo(Path.Combine(SysClassNet, nic.Name));
        return link.Exists && IsPhysicalWired(link.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? link.FullName);
    }

    public async ValueTask<IReadOnlyList<Neighbour>> Neighbours(IReadOnlyList<IPAddress> gateways, CancellationToken cancel) =>
        File.Exists(ProcNetArp) ? ParseArp(await File.ReadAllTextAsync(ProcNetArp, cancel)) : [];

    public void Resolve(IReadOnlyList<IPAddress> gateways)
    {
        foreach (var gateway in gateways) NetworkLinkReader.Poke(gateway);
    }

    // the interface's device directory, symlinks resolved: bridges, veths, tuns and the like live under /devices/virtual
    internal static bool IsPhysicalWired(string deviceDir)
    {
        if (deviceDir.Replace('\\', '/').Contains("/devices/virtual/")) return false;
        if (Directory.Exists(Path.Combine(deviceDir, "wireless")) || Directory.Exists(Path.Combine(deviceDir, "phy80211"))) return false;
        var type = Path.Combine(deviceDir, "type");
        return File.Exists(type) && File.ReadAllText(type).Trim() == EtherType;
    }

    // IP address, HW type, Flags, HW address, Mask, Device, under a header line
    internal static List<Neighbour> ParseArp(string table)
    {
        var neighbours = new List<Neighbour>();
        foreach (var line in table.Split('\n').Skip(1))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 4) continue;
            if (!int.TryParse(fields[2].Replace("0x", ""), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var flags) || (flags & ArpComplete) == 0) continue;
            if (!IPAddress.TryParse(fields[0], out var address) || NetworkLinks.ParseMac(fields[3]) is not { } mac) continue;
            neighbours.Add(new Neighbour(address, mac));
        }
        return neighbours;
    }
}
