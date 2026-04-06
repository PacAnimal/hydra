using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Cathedral.Utils;
using Hydra.Config;
using Hydra.Platform;
using Microsoft.Extensions.Logging;

namespace Hydra.Update;

internal sealed class SelfUpdater(HydraConfig config, ILogger<SelfUpdater> log) : SimpleHostedService(log, TimeSpan.FromHours(1))
{
    private const string Repo = "pacanimal/hydra";
    private readonly Toggle _warned = new();

    protected override async Task Execute(CancellationToken cancel)
    {
        Cleanup();

        if (!config.AutoUpdate)
        {
            if (_warned.TrySet()) log.LogDebug("auto-update disabled");
            return;
        }

        if (Debugger.IsAttached)
        {
            if (_warned.TrySet()) log.LogInformation("auto-update skipped (debugger attached)");
            return;
        }

        try
        {
            await CheckAndUpdate(cancel);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "auto-update failed, continuing with current version");
        }
    }

    private async Task CheckAndUpdate(CancellationToken cancel)
    {
        var current = CurrentVersion();
        log.LogDebug("checking for updates (current: {Version})", current);

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Hydra");
        http.Timeout = TimeSpan.FromSeconds(30);

        var json = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest", cancel);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var tag = root.GetProperty("tag_name").GetString() ?? "0.0.0";
        var latest = Version.Parse(tag.TrimStart('v'));

        if (latest <= current)
        {
            log.LogDebug("already up to date ({Version})", current);
            return;
        }

        log.LogInformation("update available: {Current} → {Latest}", current, latest);

        var rid = Rid();
        if (rid == null)
        {
            log.LogWarning("unsupported platform for auto-update");
            return;
        }

        var assetName = $"hydra-{rid}.tar.gz";
        string? downloadUrl = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() == assetName)
            {
                downloadUrl = asset.GetProperty("browser_download_url").GetString();
                break;
            }
        }

        if (downloadUrl == null)
        {
            log.LogWarning("no asset found for {Asset}", assetName);
            return;
        }

        log.LogInformation("downloading {Asset}", assetName);
        await DownloadAndApply(http, downloadUrl, cancel);
    }

    private async Task DownloadAndApply(HttpClient http, string url, CancellationToken cancel)
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("cannot determine process path");
        var appDir = Path.GetDirectoryName(exePath)
            ?? throw new InvalidOperationException("cannot determine app directory");

        var exeName = OperatingSystem.IsWindows() ? "Hydra.exe" : "Hydra";
        var tmpPath = Path.Combine(appDir, exeName + ".tmp");

        // stream: http → gzip → tar → .tmp file
        using var downloadCancel = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        downloadCancel.CancelAfter(TimeSpan.FromMinutes(2));

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, downloadCancel.Token);
        response.EnsureSuccessStatusCode();

        await using var httpStream = await response.Content.ReadAsStreamAsync(downloadCancel.Token);
        await using var gzip = new GZipStream(httpStream, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);

        TarEntry? entry;
        while ((entry = await tar.GetNextEntryAsync(cancellationToken: downloadCancel.Token)) != null)
        {
            if (Path.GetFileName(entry.Name) != exeName || entry.DataStream == null) continue;
            await using var tmp = File.Create(tmpPath);
            await entry.DataStream.CopyToAsync(tmp, downloadCancel.Token);
            break;
        }

        if (!File.Exists(tmpPath))
            throw new InvalidOperationException($"'{exeName}' not found in archive");

        // atomic swap
        if (OperatingSystem.IsWindows())
        {
            File.Move(exePath, exePath + ".old");
            File.Move(tmpPath, exePath);
        }
        else
        {
            File.Move(tmpPath, exePath, overwrite: true);
            var mode = File.GetUnixFileMode(exePath);
            File.SetUnixFileMode(exePath, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }

        log.LogInformation("update applied, restarting");
        ProcessRestart.Restart();
    }

    private static void Cleanup()
    {
        var appDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (appDir == null) return;
        foreach (var file in Directory.EnumerateFiles(appDir, "*.tmp").Concat(Directory.EnumerateFiles(appDir, "*.old")))
        {
            try { File.Delete(file); }
            catch { /* best effort */ }
        }
    }

    private static Version CurrentVersion() =>
        Version.Parse(Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion?.Split('+')[0]  // strip build metadata suffix
            ?? "0.0.0");

    private static string? Rid()
    {
        if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64) return "osx-arm64";
        if (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64) return "win-x64";
        if (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64) return "linux-x64";
        return null;
    }
}
