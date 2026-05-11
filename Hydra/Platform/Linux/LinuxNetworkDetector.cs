using System.Text;
using Cathedral.Utils;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Linux;

internal sealed class LinuxNetworkDetector(ICmdRunner cmd, ILogger<LinuxNetworkDetector> log) : INetworkDetector
{
    public async Task<List<string>> GetActiveSsids(CancellationToken cancel = default)
    {
        var ssid = await GetSsid(cancel);
        return ssid != null ? [ssid] : [];
    }

    public Task<bool?> GetIsPluggedIn(CancellationToken cancel = default) =>
        Task.FromResult(ReadIsPluggedIn());

    // reads /sys/class/power_supply/ to find the first Mains adapter and its online state
    private bool? ReadIsPluggedIn()
    {
        const string basePath = "/sys/class/power_supply";
        try
        {
            if (!Directory.Exists(basePath)) return null;
            foreach (var dir in Directory.GetDirectories(basePath))
            {
                var typePath = Path.Combine(dir, "type");
                if (!File.Exists(typePath) || File.ReadAllText(typePath).Trim() != "Mains") continue;
                var onlinePath = Path.Combine(dir, "online");
                if (!File.Exists(onlinePath)) continue;
                return File.ReadAllText(onlinePath).Trim() switch { "1" => true, "0" => false, _ => null };
            }
        }
        catch (Exception e) { log.LogWarning("Failed to get power state: {Message}", e.Message); }
        return null;
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
}
