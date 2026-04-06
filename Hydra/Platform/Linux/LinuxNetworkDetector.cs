using System.Net.NetworkInformation;
using System.Text;
using Cathedral.Utils;
using Hydra.Config;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Linux;

internal sealed class LinuxNetworkDetector(ICmdRunner cmd, ILogger<LinuxNetworkDetector> log) : INetworkDetector
{
    public async Task<List<NetworkState>> GetActiveNetworks(CancellationToken cancel = default)
    {
        var results = new List<NetworkState>();

        var ssid = await GetSsid(cancel);
        if (ssid != null)
            results.Add(new NetworkState(ConfigCondition.Ssid, ssid));

        // wired: active Ethernet interface that isn't a wireless one
        var wirelessNames = await GetWirelessInterfaceNames();
        foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (iface.OperationalStatus != OperationalStatus.Up) continue;
            if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (wirelessNames.Contains(iface.Name)) continue;
            if (iface.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Unknown)) continue;
            results.Add(new NetworkState(ConfigCondition.Wired, null));
            break;
        }

        return results;
    }

    // iwgetid -r outputs raw SSID on stdout, empty if not connected
    private async Task<string?> GetSsid(CancellationToken cancel)
    {
        try
        {
            var output = new StringBuilder();
            var exitCode = await cmd.TextCommand("iwgetid", ["-r"], ".",
                o => { if (o.Source == ICmdRunner.OutputSource.StdOut) output.AppendLine(o.Text); },
                _ => { }, cancel);

            if (exitCode != 0) return null;
            var ssid = output.ToString().Trim();
            return string.IsNullOrEmpty(ssid) ? null : ssid;
        }
        catch (Exception e) { log.LogWarning("Failed to get ssid from iwgetid: {Message}", e.Message); }
        return null;
    }

    // reads /proc/net/wireless to get a list of wireless interface names
    private static Task<HashSet<string>> GetWirelessInterfaceNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var line in File.ReadLines("/proc/net/wireless"))
            {
                var trimmed = line.TrimStart();
                // lines look like "  wlan0: ..."
                var colon = trimmed.IndexOf(':');
                if (colon > 0)
                    names.Add(trimmed[..colon].Trim());
            }
        }
        catch { /* file may not exist if no wireless adapters */ }
        return Task.FromResult(names);
    }
}
