using System.Net.NetworkInformation;
using Cathedral.Utils;
using Hydra.Platform;
using Microsoft.Extensions.Logging;

namespace Hydra.Config;

// monitors network changes and restarts hydra when the active config should change
internal sealed class NetworkWatcher : SimpleHostedService
{
    private readonly INetworkDetector _detector;
    private readonly List<HydraConfig> _configs;
    private readonly HydraConfig? _activeConfig;
    private readonly ILogger<NetworkWatcher> _log;

    // tracks last known SSIDs for transition logging
    private List<string>? _lastSsids;

    // debounce: ignore rapid re-triggers within this window
    private DateTime _lastCheck = DateTime.MinValue;
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);

    public NetworkWatcher(INetworkDetector detector, List<HydraConfig> configs, HydraConfig? activeConfig, ILogger<NetworkWatcher> log)
        : base(log, TimeSpan.FromSeconds(60))
    {
        _detector = detector;
        _configs = configs;
        _activeConfig = activeConfig;
        _log = log;

        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
    }

    protected override async Task Execute(CancellationToken cancel)
    {
        await CheckNetwork(cancel);
    }

    protected override Task OnShutdown(CancellationToken cancel)
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        return Task.CompletedTask;
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e) => _ = CheckNetwork(CancellationToken.None);

    // called by MacShieldProcess when network state changes post-startup
    internal void TriggerCheck() => _ = CheckNetwork(CancellationToken.None);

    private async Task CheckNetwork(CancellationToken cancel)
    {
        // no conditional configs — nothing to check
        if (!HydraConfig.HasConditions(_configs)) return;

        // debounce rapid-fire events
        var now = DateTime.UtcNow;
        if (now - _lastCheck < Debounce) return;
        _lastCheck = now;

        List<string> ssids;
        try
        {
            ssids = await _detector.GetActiveSsids(cancel);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogWarning(e, "network detection failed");
            return;
        }

        // log transition (null on first check = startup)
        LogTransition(_lastSsids, ssids);
        _lastSsids = ssids;

        var resolved = HydraConfig.Resolve(_configs, ssids);
        if (resolved == _activeConfig) return;

        var from = _activeConfig != null ? $"{_activeConfig.Mode}" : "idle";
        var to = resolved != null ? $"{resolved.Mode}" : "idle";
        _log.LogInformation("Network change: switching from {From} to {To}, restarting", from, to);
        ProcessRestart.Restart();
    }

    private void LogTransition(List<string>? previous, List<string> current)
    {
        var prevStr = FormatSsids(previous);
        var currStr = FormatSsids(current);
        if (prevStr == currStr) return;
        _log.LogInformation("Network: {Previous} → {Current}", prevStr, currStr);
    }

    private static string FormatSsids(List<string>? ssids)
    {
        if (ssids == null) return "null";
        if (ssids.Count == 0) return "none";
        return string.Join(", ", ssids.Select(s => $"WiFi ({s})"));
    }
}
