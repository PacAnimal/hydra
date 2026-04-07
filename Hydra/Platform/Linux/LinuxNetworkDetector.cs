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
