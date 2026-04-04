using Cathedral.Extensions;
using Cathedral.Utils;
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
    private readonly IWorldState _peerState;

    private readonly SemaphoreSlimValue<LocalSlaveState> _state = new(new LocalSlaveState(), disposeValue: false);

    // ReSharper disable once ConvertToPrimaryConstructor
#pragma warning disable IDE0290
    public SlaveRelayConnection(HydraConfig config, ILogger<RelayConnection> log, IPlatformOutput output, SlaveLogForwarder logForwarder, IWorldState peerState)
        : base(config, log, peerState)
    {
        _output = output;
        _logForwarder = logForwarder;
        _log = log;
        _config = config;
        _peerState = peerState;
    }
#pragma warning restore IDE0290

    protected override async Task OnAuthenticated()
    {
        var detected = _output.GetAllScreens();
        using var s = await _state.WaitForDisposable();
        RebuildScreenInfo(s.Value, detected);
        _log.LogInformation("Local screens: {Count}", s.Value.Entries.Count);
    }

    protected override async Task OnReceive(string sourceHost, MessageKind kind, string json)
    {
        switch (kind)
        {
            case MessageKind.MasterConfig:
                await HandleMasterConfig(sourceHost);
                break;
            case MessageKind.MouseMove:
                var move = json.FromSaneJson<MouseMoveMessage>();
                if (move != null) await MoveToScreen(move.Screen, move.X, move.Y);
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
                if (enter != null) await MoveToScreen(enter.Screen, enter.X, enter.Y);
                break;
            case MessageKind.LeaveScreen:
                break;
            default:
                _log.LogDebug("Unhandled message kind {Kind} from {Host}", kind, sourceHost);
                break;
        }
    }

    protected override async Task OnPeers(string[] hostNames)
    {
        var current = new HashSet<string>(hostNames, StringComparer.OrdinalIgnoreCase);
        await _peerState.PruneMasters(current);
        await base.OnPeers(hostNames);
    }

    private async Task HandleMasterConfig(string masterHost)
    {
        await _peerState.AddMaster(masterHost);
        List<ScreenInfoEntry> entries;
        using (var s = await _state.WaitForDisposable())
            entries = s.Value.Entries;
        SendScreenInfo(masterHost, entries);
    }

    private async Task MoveToScreen(string screenName, int x, int y)
    {
        ScreenRect? screen;
        using (var s = await _state.WaitForDisposable())
            s.Value.Map.TryGetValue(screenName, out screen);

        if (screen != null)
            _output.MoveMouse(screen.X + x, screen.Y + y);
        else
            _output.MoveMouse(x, y);
    }

    private void SendScreenInfo(string masterHost, List<ScreenInfoEntry> entries)
    {
        _log.LogInformation("Sending screen info to {Master}: {Count} screen(s)", masterHost, entries.Count);
        var payload = MessageSerializer.Encode(MessageKind.ScreenInfo, new ScreenInfoMessage(entries));
        _ = Send([masterHost], payload).AsTask();
    }

    // must be called under _state lock
    private void RebuildScreenInfo(LocalSlaveState s, List<DetectedScreen> detected)
    {
        s.DetectedScreens = detected;

        if (detected.Count == 0)
        {
            s.Entries = [];
            s.Map = new Dictionary<string, ScreenRect>(StringComparer.OrdinalIgnoreCase);
            return;
        }

        var minX = detected.Min(d => d.X);
        var minY = detected.Min(d => d.Y);

        var entries = new List<ScreenInfoEntry>();
        var map = new Dictionary<string, ScreenRect>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < detected.Count; i++)
        {
            var d = detected[i];
            var name = ScreenNaming.BuildScreenName(_config.ResolvedName, i, detected.Count);
            var scale = ResolveScale(d);
            var nx = d.X - minX;
            var ny = d.Y - minY;
            entries.Add(new ScreenInfoEntry(name, nx, ny, d.Width, d.Height, scale));
            map[name] = new ScreenRect(name, _config.ResolvedName, d.X, d.Y, d.Width, d.Height, IsLocal: true);
        }

        s.Entries = entries;
        s.Map = map;
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

    private static bool Matches(DetectedScreen d, string match) =>
        d.DisplayName?.EqualsIgnoreCase(match) is true
        || d.OutputName?.EqualsIgnoreCase(match) is true
        || d.PlatformId?.EqualsIgnoreCase(match) is true;

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

                string[]? masters = null;
                List<ScreenInfoEntry>? entries = null;
                using (var s = await _state.WaitForDisposable(ct))
                {
                    if (ScreenRect.ScreenListChanged(current, s.Value.DetectedScreens))
                    {
                        RebuildScreenInfo(s.Value, current);
                        entries = s.Value.Entries;
                    }
                }

                if (entries != null)
                    masters = await _peerState.GetMasters();

                if (masters != null)
                {
                    _log.LogInformation("Slave screen configuration changed — re-sending screen info");
                    foreach (var master in masters)
                        SendScreenInfo(master, entries!);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private class LocalSlaveState
    {
        public List<DetectedScreen> DetectedScreens = [];
        public List<ScreenInfoEntry> Entries = [];
        public Dictionary<string, ScreenRect> Map = new(StringComparer.OrdinalIgnoreCase);
    }
}
