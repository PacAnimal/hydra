using Cathedral.Extensions;
using Hydra.Config;
using Hydra.Platform;
using Hydra.Screen;
using Microsoft.Extensions.Logging;

namespace Hydra.Relay;

public sealed class SlaveRelayConnection : RelayConnection
{
    private readonly IPlatformOutput _output;
    private readonly SlaveLogForwarder _logForwarder;
    private readonly ILogger<RelayConnection> _log;
    private readonly HydraConfig _config;

    // local screen state (refreshed on change)
    private List<DetectedScreen> _detectedScreens = [];
    private List<ScreenInfoEntry> _screenEntries = [];
    private Dictionary<string, ScreenRect> _screenMap = [];

    // connected masters to re-notify on screen change
    private readonly HashSet<string> _masters = [];

    // ReSharper disable once ConvertToPrimaryConstructor
#pragma warning disable IDE0290
    public SlaveRelayConnection(HydraConfig config, ILogger<RelayConnection> log, IPlatformOutput output, SlaveLogForwarder logForwarder)
        : base(config, log)
    {
        _output = output;
        _logForwarder = logForwarder;
        _log = log;
        _config = config;
    }
#pragma warning restore IDE0290

    protected override void OnAuthenticated()
    {
        RefreshScreenInfo();
        _log.LogInformation("Local screens: {Count}", _screenEntries.Count);
    }

    protected override Task OnReceive(string sourceHost, MessageKind kind, string json)
    {
        switch (kind)
        {
            case MessageKind.MasterConfig:
                HandleMasterConfig(sourceHost);
                break;
            case MessageKind.MouseMove:
                var move = json.FromSaneJson<MouseMoveMessage>();
                if (move != null) HandleMouseMove(move);
                break;
            case MessageKind.KeyEvent:
                var key = json.FromSaneJson<KeyEventMessage>();
                if (key != null) _output.InjectKey(key);
                break;
            case MessageKind.MouseButton:
                var btn = json.FromSaneJson<MouseButtonMessage>();
                if (btn != null) _output.InjectMouseButton(btn);
                break;
            case MessageKind.MouseScroll:
                var scroll = json.FromSaneJson<MouseScrollMessage>();
                if (scroll != null) _output.InjectMouseScroll(scroll);
                break;
            case MessageKind.EnterScreen:
                var enter = json.FromSaneJson<EnterScreenMessage>();
                if (enter != null) HandleEnterScreen(enter);
                break;
            case MessageKind.LeaveScreen:
                break;
        }
        return Task.CompletedTask;
    }

    private void HandleMasterConfig(string masterHost)
    {
        _logForwarder.AddMaster(masterHost);
        _masters.Add(masterHost);
        SendScreenInfo(masterHost);
    }

    private void HandleMouseMove(MouseMoveMessage move)
    {
        if (_screenMap.TryGetValue(move.Screen, out var screen))
            _output.MoveMouse(screen.X + move.X, screen.Y + move.Y);
        else
            _output.MoveMouse(move.X, move.Y);
    }

    private void HandleEnterScreen(EnterScreenMessage enter)
    {
        if (_screenMap.TryGetValue(enter.Screen, out var screen))
            _output.MoveMouse(screen.X + enter.X, screen.Y + enter.Y);
        else
            _output.MoveMouse(enter.X, enter.Y);
    }

    private void SendScreenInfo(string masterHost)
    {
        _log.LogInformation("Sending screen info to {Master}: {Count} screen(s)", masterHost, _screenEntries.Count);
        var payload = MessageSerializer.Encode(MessageKind.ScreenInfo, new ScreenInfoMessage(_screenEntries));
        _ = Send([masterHost], payload).AsTask();
    }

    // builds screen entries from OS-detected screens, matching ScreenDefinitions for scale
    private void RefreshScreenInfo()
    {
        var detected = _output.GetAllScreens();
        _detectedScreens = detected;

        if (detected.Count == 0)
        {
            _screenEntries = [];
            _screenMap = [];
            return;
        }

        // normalize positions: compute bounding rect origin
        var minX = detected.Min(d => d.X);
        var minY = detected.Min(d => d.Y);

        _screenEntries = [];
        _screenMap = [];

        for (var i = 0; i < detected.Count; i++)
        {
            var d = detected[i];
            var name = detected.Count == 1 ? _config.ResolvedName : $"{_config.ResolvedName}:{i}";
            var scale = ResolveScale(d);

            // normalized X/Y relative to bounding rect origin
            var nx = d.X - minX;
            var ny = d.Y - minY;

            _screenEntries.Add(new ScreenInfoEntry(name, nx, ny, d.Width, d.Height, scale));
            _screenMap[name] = new ScreenRect(name, _config.ResolvedName, d.X, d.Y, d.Width, d.Height, IsLocal: true);
        }
    }

    // match a detected screen against ScreenDefinitions for per-screen scale
    private decimal ResolveScale(DetectedScreen d)
    {
        foreach (var def in _config.ScreenDefinitions)
        {
            if (Matches(d, def.Match))
                return def.Scale;
        }
        return 1.0m;
    }

    private static bool Matches(DetectedScreen d, string match)
    {
        var cmp = StringComparison.OrdinalIgnoreCase;
        return (d.DisplayName != null && d.DisplayName.Equals(match, cmp))
            || (d.OutputName != null && d.OutputName.Equals(match, cmp))
            || (d.PlatformId != null && d.PlatformId.Equals(match, cmp));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.WhenAll(
            base.ExecuteAsync(stoppingToken),
            PollScreenChangesAsync(stoppingToken));
    }

    // poll for screen configuration changes and re-notify masters
    private async Task PollScreenChangesAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var current = _output.GetAllScreens();
                if (!ScreenListChanged(current, _detectedScreens))
                    continue;

                _log.LogInformation("Slave screen configuration changed — re-sending screen info");
                RefreshScreenInfo();
                foreach (var master in _masters)
                    SendScreenInfo(master);
            }
        }
        catch (OperationCanceledException) { }
    }

    private static bool ScreenListChanged(List<DetectedScreen> a, List<DetectedScreen> b)
    {
        if (a.Count != b.Count) return true;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].X != b[i].X || a[i].Y != b[i].Y || a[i].Width != b[i].Width || a[i].Height != b[i].Height)
                return true;
        }
        return false;
    }
}
