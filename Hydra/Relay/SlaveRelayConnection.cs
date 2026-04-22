using Cathedral.Extensions;
using Hydra.Config;
using Hydra.FileTransfer;
using Hydra.Keyboard;
using Hydra.Platform;
using Hydra.Screen;
using Microsoft.Extensions.Logging;

namespace Hydra.Relay;

public class SlaveRelayConnection : RelayConnection
{
    private readonly IPlatformOutput _output;
    private readonly ILogger<RelayConnection> _log;
    private readonly IScreenDetector _screens;
    private readonly IWorldState _peerState;
    private readonly SlaveCursorHider _cursorHider;
    private readonly IScreenSaverSync _screenSaverSync;
    private readonly IScreensaverSuppressor _screensaverSuppressor;
    private readonly IClipboardSync _clipboardSync;
    private readonly FileTransferService _fileTransfer;
    private readonly IFileSelectionDetector _selectionDetector;
    private readonly IOsdNotification _osd;

    // active key repeat timers keyed by (char?, SpecialKey?)
    private readonly Dictionary<(char?, SpecialKey?), CancellationTokenSource> _repeatTimers = [];

    // keys currently held down on the slave (for release-all on screen leave)
    private readonly HashSet<(char?, SpecialKey?)> _heldKeys = [];

    // fallback clipboard when Get* returns null because we own the selection (echo suppression)
    private ClipboardSnapshot? _lastPushed;

    // cached screen layout for synchronous mouse move handling (avoids async overhead on the relay hot path)
    private volatile LocalScreenSnapshot? _cachedScreens;

    // ReSharper disable once ConvertToPrimaryConstructor
#pragma warning disable IDE0290
    public SlaveRelayConnection(IHydraProfile profile, ILogger<RelayConnection> log, IPlatformOutput output, IScreenDetector screens, IWorldState peerState, SlaveCursorHider cursorHider, IScreenSaverSync screenSaverSync, IScreensaverSuppressor screensaverSuppressor, IClipboardSync clipboardSync, FileTransferService fileTransfer, IFileSelectionDetector selectionDetector, IOsdNotification osd)
        : base(profile, log, peerState)
    {
        _output = output;
        _log = log;
        _screens = screens;
        _peerState = peerState;
        _cursorHider = cursorHider;
        _screenSaverSync = screenSaverSync;
        _screensaverSuppressor = screensaverSuppressor;
        _clipboardSync = clipboardSync;
        _fileTransfer = fileTransfer;
        _selectionDetector = selectionDetector;
        _osd = osd;

        _screens.ScreensChanged += async snapshot =>
        {
            _cachedScreens = snapshot;
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
        _cachedScreens = snapshot;
        _log.LogInformation("Local screens: {Count}", snapshot.Screens.Count);
    }

    protected override async Task OnReceive(string sourceHost, MessageKind kind, string json)
    {
        switch (kind)
        {
            case MessageKind.MasterConfig:
                await HandleMasterConfig(sourceHost, json);
                break;
            case MessageKind.MouseMove:
                var move = Parse<MouseMoveMessage>(json, kind);
                if (move != null)
                {
                    _cursorHider.OnMasterActivity(sourceHost);
                    MoveToCachedScreen(move.Screen, move.X, move.Y);
                }
                break;
            case MessageKind.KeyEvent:
                var key = Parse<KeyEventMessage>(json, kind);
                if (key != null)
                {
                    _cursorHider.OnMasterActivity(sourceHost);
                    HandleKeyEvent(key);
                }
                break;
            case MessageKind.MouseMoveDelta:
                var delta = Parse<MouseMoveDeltaMessage>(json, kind);
                if (delta != null)
                {
                    _cursorHider.OnMasterActivity(sourceHost);
                    _output.MoveMouseRelative(delta.Dx, delta.Dy);
                }
                break;
            case MessageKind.MouseButton:
                var btn = Parse<MouseButtonMessage>(json, kind);
                if (btn != null)
                {
                    _cursorHider.OnMasterActivity(sourceHost);
                    _output.InjectMouseButton(btn);
                }
                break;
            case MessageKind.MouseScroll:
                var scroll = Parse<MouseScrollMessage>(json, kind);
                if (scroll != null)
                {
                    _cursorHider.OnMasterActivity(sourceHost);
                    _output.InjectMouseScroll(scroll);
                }
                break;
            case MessageKind.EnterScreen:
                var enter = Parse<EnterScreenMessage>(json, kind);
                if (enter != null)
                {
                    MoveToCachedScreen(enter.Screen, enter.X, enter.Y);
                    _cursorHider.OnEnterScreen(sourceHost);
                }
                break;
            case MessageKind.LeaveScreen:
                ReleaseAllKeys();
                CancelAllRepeatTimers();
                _cursorHider.OnLeaveScreen(sourceHost);
                break;
            case MessageKind.ScreensaverSync:
                var ss = Parse<ScreensaverSyncMessage>(json, kind);
                if (ss != null)
                {
                    _log.LogInformation("Screensaver sync from {Host}: active={Active}", sourceHost, ss.Active);
                    if (ss.Active) _screenSaverSync.Activate();
                    else _screenSaverSync.Deactivate();
                }
                break;
            case MessageKind.ClipboardPush:
                var push = Parse<ClipboardPushMessage>(json, kind);
                if (push != null)
                {
                    _log.LogDebug("Clipboard push from {Host}: text={TextLen}, primary={PrimaryLen}, image={ImageLen}",
                        sourceHost, push.Text.Length, push.PrimaryText?.Length, push.ImagePng?.Length);
                    var validated = ClipboardUtils.ValidateFields(push.Text, push.PrimaryText, push.ImagePng, _log, "push", sourceHost);
                    _lastPushed = validated;
                    _clipboardSync.SetClipboard(validated.Text, validated.PrimaryText, validated.ImagePng);
                }
                break;
            case MessageKind.ClipboardPull:
                _log.LogDebug("Clipboard pull from {Host}", sourceHost);
                var pullClip = ClipboardUtils.ReadWithFallback(_clipboardSync, _lastPushed, _log, "pull response");
                _log.LogDebug("Pull response: text={TextLen}, primary={PrimaryLen}, image={ImageLen}", pullClip.Text?.Length, pullClip.PrimaryText?.Length, pullClip.ImagePng?.Length);
                var response = MessageSerializer.Encode(MessageKind.ClipboardPullResponse, new ClipboardPullResponseMessage(pullClip.Text, pullClip.PrimaryText, pullClip.ImagePng));
                Send([sourceHost], response);
                break;
            case MessageKind.Osd:
                {
                    var osdMsg = Parse<OsdMessage>(json, kind);
                    if (osdMsg != null) _osd.Show(osdMsg.Text);
                    break;
                }
            case MessageKind.FileSelectionQuery:
                {
                    if (_fileTransfer.FileTransferOngoing)
                    {
                        _log.LogInformation("File selection query from {Host} refused: transfer already in progress", sourceHost);
                        Send([sourceHost], MessageSerializer.Encode(MessageKind.FileTransferBusy, new FileTransferBusyMessage()));
                        break;
                    }
                    if (!_selectionDetector.IsFileTransferSupported)
                    {
                        _log.LogInformation("File selection query from {Host}: file transfer not supported on this platform", sourceHost);
                        var unsupportedPayload = MessageSerializer.Encode(MessageKind.FileSelectionResponse, new FileSelectionResponseMessage(null, "Action not supported"));
                        Send([sourceHost], unsupportedPayload);
                        break;
                    }
                    var result = _selectionDetector.GetSelectedPaths();
                    if (!result.FileManagerFocused)
                        _log.LogInformation("File selection query from {Host}: {Name} is not focused", sourceHost, _selectionDetector.FileManagerName);
                    else if (result.Paths != null)
                        _log.LogInformation("File selection query from {Host}: {Count} file(s) selected: {Paths}", sourceHost, result.Paths.Count, string.Join(", ", result.Paths));
                    else
                        _log.LogInformation("File selection query from {Host}: no files selected", sourceHost);
                    var notFocused = result.FileManagerFocused ? null : $"{_selectionDetector.FileManagerName} is not focused";
                    var selectionPayload = MessageSerializer.Encode(MessageKind.FileSelectionResponse, new FileSelectionResponseMessage(result.Paths?.ToArray(), notFocused));
                    Send([sourceHost], selectionPayload);
                    break;
                }
            case MessageKind.FileStreamRequest:
                {
                    if (_fileTransfer.FileTransferOngoing)
                    {
                        _log.LogInformation("Stream request from {Host} refused: transfer already in progress", sourceHost);
                        Send([sourceHost], MessageSerializer.Encode(MessageKind.FileTransferBusy, new FileTransferBusyMessage()));
                        break;
                    }
                    var req = Parse<FileStreamRequestMessage>(json, kind);
                    if (req != null)
                        _ = _fileTransfer.StreamToHost(req.Paths, req.TargetHost, this);
                    break;
                }
            case var _ when FileTransferService.IsFileTransferMessage(kind):
                await _fileTransfer.OnMessageAsync(sourceHost, kind, json, this);
                break;
            default:
                _log.LogDebug("Unhandled message kind {Kind} from {Host}", kind, sourceHost);
                break;
        }
    }

    protected override async Task OnDisconnected()
    {
        _fileTransfer.Abort(this, "relay disconnected");
        var masters = await _peerState.GetMasters();
        foreach (var master in masters)
            _cursorHider.OnMasterDisconnected(master);
        ReleaseAllKeys();
        CancelAllRepeatTimers();
        if (masters.Length > 0)
            _screensaverSuppressor.Restore();
        await _peerState.PruneMasters([]);
        _log.LogWarning("Relay connection lost — cursor restored on slave");
    }

    protected override async Task OnPeers(string[] hostNames)
    {
        var current = new HashSet<string>(hostNames, StringComparer.OrdinalIgnoreCase);
        var before = await _peerState.GetMasters();
        await _peerState.PruneMasters(current);
        var after = await _peerState.GetMasters();
        var afterSet = new HashSet<string>(after, StringComparer.OrdinalIgnoreCase);
        var anyMasterLeft = false;
        foreach (var departed in before.Where(h => !afterSet.Contains(h)))
        {
            _cursorHider.OnMasterDisconnected(departed);
            anyMasterLeft = true;
        }
        // restore screensaver suppression when all masters have disconnected
        if (anyMasterLeft && after.Length == 0)
            _screensaverSuppressor.Restore();
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
            catch (Exception ex) { _log.LogWarning(ex, "Key repeat timer error for {Key}", repeatKey); }
        }, ct);
    }

    private T? Parse<T>(string json, MessageKind kind) where T : class
    {
        var result = json.FromSaneJson<T>();
        if (result == null) _log.LogWarning("Failed to deserialize {Kind} message — dropping", kind);
        return result;
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

    private async Task HandleMasterConfig(string masterHost, string json)
    {
        var config = json.FromSaneJson<MasterConfigMessage>() ?? new MasterConfigMessage(null);
        var before = await _peerState.GetMasters();
        await _peerState.AddMaster(masterHost, config);
        var after = await _peerState.GetMasters();
        // only signal connected if this is a genuinely new master
        if (after.Length > before.Length)
        {
            _cursorHider.OnMasterConnected();
            _screensaverSuppressor.Suppress();
        }
        var snapshot = await _screens.Get();
        SendScreenInfo(masterHost, snapshot.Entries);
    }

    private void MoveToCachedScreen(string screenName, int x, int y)
    {
        var snapshot = _cachedScreens;
        var screen = snapshot?.Screens.FirstOrDefault(s => s.Name.EqualsIgnoreCase(screenName));
        if (screen != null)
            _output.MoveMouse(screen.X + x, screen.Y + y);
        else
            _output.MoveMouse(x, y);
    }

    private void SendScreenInfo(string masterHost, List<ScreenInfoEntry> entries)
    {
        _log.LogInformation("Sending screen info to {Master}: {Count} screen(s)", masterHost, entries.Count);
        var platform = DetectLocalPlatform();
        var payload = MessageSerializer.Encode(MessageKind.ScreenInfo, new ScreenInfoMessage(entries, platform));
        Send([masterHost], payload);
    }

    private static PeerPlatform DetectLocalPlatform() =>
        OperatingSystem.IsLinux() ? PeerPlatform.Linux :
        OperatingSystem.IsMacOS() ? PeerPlatform.MacOS :
        OperatingSystem.IsWindows() ? PeerPlatform.Windows :
        PeerPlatform.Unknown;
}
