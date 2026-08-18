using System.Net.NetworkInformation;
using Cathedral.Extensions;
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
    private readonly IDormancyState _dormancy;
    private readonly Action _restart;
    private readonly Func<DateTime> _now;
    private readonly ILogger<NetworkWatcher> _log;

    // tracks last known state for transition logging
    private List<string>? _lastSsids;
    private int? _lastScreenCount;
    private bool? _lastIsPluggedIn;

    // debounce: ignore rapid re-triggers within this window
    private DateTime _lastCheck = DateTime.MinValue;
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);
    private readonly Toggle _checking = new(); // set while a check is running — serializes concurrent callers

    public NetworkWatcher(INetworkDetector detector, Func<int> screenCountProvider, List<HydraConfig> configs, HydraConfig? activeConfig, string? profileOverride, IDormancyState dormancy, ILogger<NetworkWatcher> log, Action? restart = null, Func<DateTime>? clock = null)
        : base(log, TimeSpan.FromSeconds(10))
    {
        _detector = detector;
        _screenCountProvider = screenCountProvider;
        _configs = configs;
        _activeConfig = activeConfig;
        _profileOverride = profileOverride;
        _dormancy = dormancy;
        _restart = restart ?? ProcessRestart.Restart;
        _now = clock ?? (() => DateTime.UtcNow);
        _log = log;

        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;

        // a woken machine that never got its profile back drops off the relay. Restarting is how: we come
        // back up, resolve to whatever actually matches now (usually nothing) and sit there idle and
        // disconnected, which is exactly the state a machine whose lid is still shut should be in.
        _dormancy.WakeDeadlineExpired += () =>
        {
            _restart();
            return Task.CompletedTask;
        };
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

    // called when the shield reports a network change or the screens re-enumerate post-startup
    internal Task TriggerCheck() => CheckNetwork(CancellationToken.None);

    private async Task CheckNetwork(CancellationToken cancel)
    {
        // profile override is fixed — conditions can't change the selection
        if (_profileOverride != null) return;

        // no conditional configs — nothing to check
        if (!HydraConfig.HasConditions(_configs)) return;

        // serialize: the 10s Execute loop, NetworkAddressChanged and TriggerCheck can all fire
        // concurrently. Running one check at a time makes the debounce atomic (no TOCTOU on _lastCheck)
        // and stops two checks from both resolving + restarting on the same event burst.
        if (!_checking.TrySet()) return;
        try
        {
            await CheckNetworkCore(cancel);
        }
        finally
        {
            _checking.TryReset();
        }
    }

    private async Task CheckNetworkCore(CancellationToken cancel)
    {
        // debounce rapid-fire events
        var now = _now();
        if (now - _lastCheck < Debounce) return;
        _lastCheck = now;

        List<string>? ssids;
        try
        {
            ssids = await _detector.GetActiveSsids(cancel);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogWarning(e, "network detection failed");
            return;
        }

        // null = detection unavailable (unknown) — don't treat as "no wifi" and restart to a fallback;
        // keep the current config until a real reading comes back
        if (ssids == null)
        {
            _log.LogDebug("Skipping config check — SSID detection unavailable");
            return;
        }

        var screenCount = _screenCountProvider();

        // a transient 0-screen reading (sleep/wake, dock/undock, closing the laptop lid) is a degraded
        // state, never a real target config — don't let it drive a restart to idle and straight back
        // once the displays re-enumerate. Headless Linux reports 1, so this only skips genuine no-display.
        if (screenCount <= 0)
        {
            _log.LogDebug("Skipping config check — no screens detected (transient display state)");
            return;
        }

        bool? isPluggedIn;
        try
        {
            isPluggedIn = await _detector.GetIsPluggedIn(cancel);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogWarning(e, "power detection failed");
            return;
        }

        // log transitions (null on first check = startup)
        LogSsidTransition(_lastSsids, ssids);
        LogScreenCountTransition(_lastScreenCount, screenCount);
        LogIsPluggedInTransition(_lastIsPluggedIn, isPluggedIn);
        _lastSsids = ssids;
        _lastScreenCount = screenCount;
        _lastIsPluggedIn = isPluggedIn;

        var resolved = HydraConfig.Resolve(_configs, new ConditionState(ssids, screenCount, isPluggedIn));
        if (resolved == _activeConfig)
        {
            if (_dormancy.IsDormant)
            {
                _log.LogInformation("Conditions match {Profile} again — waking from dormancy", _activeConfig?.ProfileName ?? "<unnamed>");
                await _dormancy.Exit();
            }
            return;
        }

        if (resolved == null && OnlyScreensLost(ssids, screenCount, isPluggedIn))
        {
            if (!_dormancy.IsDormant)
                _log.LogInformation("Screens no longer match {Profile} — going dormant: staying on the relay, refusing input until input wakes us", _activeConfig!.ProfileName ?? "<unnamed>");
            await _dormancy.Enter();
            return;
        }

        var from = _activeConfig != null ? $"{_activeConfig.Mode}" : "idle";
        var to = resolved != null ? $"{resolved.Mode}" : "idle";
        _log.LogInformation("Conditions changed: switching from {From} to {To}, restarting", from, to);
        _restart();
    }

    // dormancy covers exactly one cause: displays going away. That is the only mismatch our own input can
    // undo, and the only one where the machine is still sitting on the desk with a master's cursor on it.
    // A changed SSID or a dropped power source means the machine really moved or was unplugged, and MORE
    // screens than the profile wants means someone opened the lid and is standing right there — restart
    // into idle for all of those and let it come up wherever it landed.
    private bool OnlyScreensLost(List<string> ssids, int screenCount, bool? isPluggedIn)
    {
        if (_activeConfig is not { Mode: Mode.Slave } active) return false;
        if (active.Conditions is not { ScreenCount: { } wanted } conditions || screenCount >= wanted) return false;
        if (conditions.Ssid is { } ssid && !ssids.Any(s => s.EqualsIgnoreCase(ssid))) return false;
        return conditions.IsPluggedIn is not { } plugged || plugged == isPluggedIn;
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

    private void LogIsPluggedInTransition(bool? previous, bool? current)
    {
        static string Format(bool? v) => v == null ? "unknown" : v.Value ? "AC" : "battery";
        if (previous == null)
        {
            // startup: log current state unless detection is unavailable
            if (current != null) _log.LogInformation("Power: {Current}", Format(current));
            return;
        }
        if (previous == current) return;
        _log.LogInformation("Power: {Previous} → {Current}", Format(previous), Format(current));
    }

    private static string FormatSsids(List<string>? ssids)
    {
        if (ssids == null) return "null";
        if (ssids.Count == 0) return "none";
        return string.Join(", ", ssids.Select(s => $"WiFi ({s})"));
    }
}
