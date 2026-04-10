using System.Net.NetworkInformation;
using Cathedral.Utils;
using Hydra.Platform;
using Microsoft.Extensions.Logging;

namespace Hydra.Config;

// monitors network/screen changes and restarts hydra when the active config should change
internal sealed class NetworkWatcher : SimpleHostedService
{
    private readonly INetworkDetector _detector;
    private readonly Func<int> _screenCountProvider;
    private readonly List<HydraConfig> _configs;
    private readonly HydraConfig? _activeConfig;
    private readonly string? _profileOverride;
    private readonly ILogger<NetworkWatcher> _log;

    // tracks last known state for transition logging
    private List<string>? _lastSsids;
    private int? _lastScreenCount;

    // debounce: ignore rapid re-triggers within this window
    private DateTime _lastCheck = DateTime.MinValue;
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);

    public NetworkWatcher(INetworkDetector detector, Func<int> screenCountProvider, List<HydraConfig> configs, HydraConfig? activeConfig, string? profileOverride, ILogger<NetworkWatcher> log)
        : base(log, TimeSpan.FromSeconds(60))
    {
        _detector = detector;
        _screenCountProvider = screenCountProvider;
        _configs = configs;
        _activeConfig = activeConfig;
        _profileOverride = profileOverride;
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
        // profile override is fixed — conditions can't change the selection
        if (_profileOverride != null) return;

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

        var screenCount = _screenCountProvider();

        // log transitions (null on first check = startup)
        LogSsidTransition(_lastSsids, ssids);
        LogScreenCountTransition(_lastScreenCount, screenCount);
        _lastSsids = ssids;
        _lastScreenCount = screenCount;

        var resolved = HydraConfig.Resolve(_configs, new ConditionState(ssids, screenCount));
        if (resolved == _activeConfig) return;

        var from = _activeConfig != null ? $"{_activeConfig.Mode}" : "idle";
        var to = resolved != null ? $"{resolved.Mode}" : "idle";
        _log.LogInformation("Conditions changed: switching from {From} to {To}, restarting", from, to);
        ProcessRestart.Restart();
    }

    private void LogSsidTransition(List<string>? previous, List<string> current)
    {
        var prevStr = FormatSsids(previous);
        var currStr = FormatSsids(current);
        if (prevStr == currStr) return;
        _log.LogInformation("Network: {Previous} → {Current}", prevStr, currStr);
    }

    private void LogScreenCountTransition(int? previous, int current)
    {
        if (previous == null || previous == current) return;
        _log.LogInformation("Screens: {Previous} → {Current}", previous, current);
    }

    private static string FormatSsids(List<string>? ssids)
    {
        if (ssids == null) return "null";
        if (ssids.Count == 0) return "none";
        return string.Join(", ", ssids.Select(s => $"WiFi ({s})"));
    }
}
