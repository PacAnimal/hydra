using Cathedral.Extensions;
using Hydra.Config;
using Hydra.Keyboard;
using Hydra.Platform;
using Hydra.Screen;
using Microsoft.Extensions.Logging;

namespace Hydra.Relay;

public sealed class SlaveRelayConnection : RelayConnection
{
    private readonly IPlatformOutput _output;
    private readonly SlaveLogForwarder _logForwarder;
    private readonly ILogger<RelayConnection> _log;
    private readonly IScreenDetector _screens;
    private readonly IWorldState _peerState;

    // active key repeat timers keyed by (char?, SpecialKey?)
    private readonly Dictionary<(char?, SpecialKey?), CancellationTokenSource> _repeatTimers = [];

    // keys currently held down on the slave (for release-all on screen leave)
    private readonly HashSet<(char?, SpecialKey?)> _heldKeys = [];

    // ReSharper disable once ConvertToPrimaryConstructor
#pragma warning disable IDE0290
    public SlaveRelayConnection(HydraConfig config, ILogger<RelayConnection> log, IPlatformOutput output, SlaveLogForwarder logForwarder, IScreenDetector screens, IWorldState peerState)
        : base(config, log, peerState)
    {
        _output = output;
        _logForwarder = logForwarder;
        _log = log;
        _screens = screens;
        _peerState = peerState;

        _screens.ScreensChanged += async snapshot =>
        {
            var masters = await _peerState.GetMasters();
            if (masters.Length > 0)
            {
                _log.LogInformation("Slave screen configuration changed — re-sending screen info");
                foreach (var master in masters)
                    SendScreenInfo(master, snapshot.Entries);
            }
        };
    }
#pragma warning restore IDE0290

    protected override async Task OnAuthenticated()
    {
        var snapshot = await _screens.Get();
        _log.LogInformation("Local screens: {Count}", snapshot.Screens.Count);
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
                if (key != null) HandleKeyEvent(key);
                break;
            case MessageKind.MouseMoveDelta:
                var delta = json.FromSaneJson<MouseMoveDeltaMessage>();
                if (delta != null) _output.MoveMouseRelative(delta.DX, delta.DY);
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
                ReleaseAllKeys();
                CancelAllRepeatTimers();
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

    private void HandleKeyEvent(KeyEventMessage msg)
    {
        var label = msg.Character.HasValue ? $" '{msg.Character}'" : msg.Key.HasValue ? $" {msg.Key}" : "";
        _log.LogDebug("Key: {Type}{Label} mods={Modifiers}", msg.Type, label, msg.Modifiers);

        var repeatKey = (msg.Character, msg.Key);

        if (msg.Type == KeyEventType.KeyUp)
        {
            // cancel any active repeat timer for this key
            if (_repeatTimers.Remove(repeatKey, out var cts))
                cts.Cancel(); // don't dispose — timer task may still be running; let GC handle it
            _heldKeys.Remove(repeatKey);
            _output.InjectKey(msg);
            return;
        }

        // KeyDown: inject immediately, then start repeat timer if settings provided
        _heldKeys.Add(repeatKey);
        _output.InjectKey(msg);

        if (msg.RepeatDelayMs is not { } delayMs || msg.RepeatRateMs is not { } rateMs) return;

        // cancel any existing timer for this key (shouldn't happen if master suppresses repeats, but be safe)
        if (_repeatTimers.Remove(repeatKey, out var existingCts))
            existingCts.Cancel();

        var repeatCts = new CancellationTokenSource();
        _repeatTimers[repeatKey] = repeatCts;
        var ct = repeatCts.Token;

        // strip repeat settings from the repeat event (downstream doesn't need them)
        var repeatMsg = msg with { RepeatDelayMs = null, RepeatRateMs = null };

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs, ct);
                while (!ct.IsCancellationRequested)
                {
                    _output.InjectKey(repeatMsg);
                    await Task.Delay(rateMs, ct);
                }
            }
            catch (OperationCanceledException) { }
        }, ct);
    }

    private void ReleaseAllKeys()
    {
        foreach (var (ch, key) in _heldKeys)
            _output.InjectKey(new KeyEventMessage(KeyEventType.KeyUp, KeyModifiers.None, ch, key));
        _heldKeys.Clear();
    }

    private void CancelAllRepeatTimers()
    {
        foreach (var cts in _repeatTimers.Values)
            cts.Cancel(); // don't dispose — timer tasks may be mid-flight; let GC handle cleanup
        _repeatTimers.Clear();
    }

    private async Task HandleMasterConfig(string masterHost)
    {
        await _peerState.AddMaster(masterHost);
        var snapshot = await _screens.Get();
        SendScreenInfo(masterHost, snapshot.Entries);
    }

    private async Task MoveToScreen(string screenName, int x, int y)
    {
        var snapshot = await _screens.Get();
        var screen = snapshot.Screens.FirstOrDefault(s => s.Name.EqualsIgnoreCase(screenName));
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
}
