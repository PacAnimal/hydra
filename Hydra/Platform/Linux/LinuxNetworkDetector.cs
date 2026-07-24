using System.Text;
using Cathedral.Utils;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Linux;

internal sealed class LinuxNetworkDetector(ICmdRunner cmd, ILogger<LinuxNetworkDetector> log) : INetworkDetector
{
    public async Task<List<string>?> GetActiveSsids(CancellationToken cancel = default)
    {
        try
        {
            var output = new StringBuilder();
            var exitCode = await cmd.TextCommand("iwgetid", ["-r"], ".",
                o => { if (o.Source == ICmdRunner.OutputSource.StdOut) output.AppendLine(o.Text); },
                _ => { }, cancel);

            // iwgetid ran: exit != 0 or empty output = genuinely not associated (known "no wifi")
            if (exitCode != 0) return [];
            var ssid = output.ToString().Trim();
            return string.IsNullOrEmpty(ssid) ? [] : [ssid];
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // couldn't even run the probe — detection is unavailable, NOT "no wifi". Return unknown so
            // a transient iwgetid spawn failure doesn't get read as "wifi gone" and trigger a restart.
            log.LogWarning("Failed to get ssid from iwgetid: {Message}", e.Message);
            return null;
        }
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

}
