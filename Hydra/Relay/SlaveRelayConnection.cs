using System.Text;
using Cathedral.Extensions;
using Hydra.Config;
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
    private readonly TempFileManager _tempFileManager;

    // active key repeat timers keyed by (char?, SpecialKey?)
    private readonly Dictionary<(char?, SpecialKey?), CancellationTokenSource> _repeatTimers = [];

    // keys currently held down on the slave (for release-all on screen leave)
    private readonly HashSet<(char?, SpecialKey?)> _heldKeys = [];

    // fallback clipboard when Get* returns null because we own the selection (echo suppression)
    private ClipboardSnapshot? _lastPushed;

    private const int MouseLogIntervalMs = 100;

    // throttle mouse receive debug logging
    private long _lastMoveLogTick;

    // ReSharper disable once ConvertToPrimaryConstructor
#pragma warning disable IDE0290
    public SlaveRelayConnection(IHydraProfile profile, ILogger<RelayConnection> log, IPlatformOutput output, IScreenDetector screens, IWorldState peerState, SlaveCursorHider cursorHider, IScreenSaverSync screenSaverSync, IScreensaverSuppressor screensaverSuppressor, IClipboardSync clipboardSync, TempFileManager tempFileManager)
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
        _tempFileManager = tempFileManager;

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
                await HandleMasterConfig(sourceHost, json);
                break;
            case MessageKind.MouseMove:
                var move = json.FromSaneJson<MouseMoveMessage>();
                if (move != null)
                {
                    _cursorHider.OnMasterActivity(sourceHost);
                    var moveNow = Environment.TickCount64;
                    if (moveNow - _lastMoveLogTick >= MouseLogIntervalMs)
                    {
                        _lastMoveLogTick = moveNow;
                        _log.LogDebug("Mouse recv: ({X}, {Y})", move.X, move.Y);
                    }
                    await MoveToScreen(move.Screen, move.X, move.Y);
                }
                break;
            case MessageKind.KeyEvent:
                var key = json.FromSaneJson<KeyEventMessage>();
                if (key != null)
                {
                    _cursorHider.OnMasterActivity(sourceHost);
                    HandleKeyEvent(key);
                }
                break;
            case MessageKind.MouseMoveDelta:
                var delta = json.FromSaneJson<MouseMoveDeltaMessage>();
                if (delta != null)
                {
                    _cursorHider.OnMasterActivity(sourceHost);
                    var deltaNow = Environment.TickCount64;
                    if (deltaNow - _lastMoveLogTick >= MouseLogIntervalMs)
                    {
                        _lastMoveLogTick = deltaNow;
                        _log.LogDebug("Mouse recv delta: ({DX}, {DY})", delta.Dx, delta.Dy);
                    }
                    _output.MoveMouseRelative(delta.Dx, delta.Dy);
                }
                break;
            case MessageKind.MouseButton:
                var btn = json.FromSaneJson<MouseButtonMessage>();
                if (btn != null)
                {
                    _cursorHider.OnMasterActivity(sourceHost);
                    _output.InjectMouseButton(btn);
                }
                break;
            case MessageKind.MouseScroll:
                var scroll = json.FromSaneJson<MouseScrollMessage>();
                if (scroll != null)
                {
                    _cursorHider.OnMasterActivity(sourceHost);
                    _output.InjectMouseScroll(scroll);
                }
                break;
            case MessageKind.EnterScreen:
                var enter = json.FromSaneJson<EnterScreenMessage>();
                if (enter != null)
                {
                    await MoveToScreen(enter.Screen, enter.X, enter.Y);
                    _cursorHider.OnEnterScreen(sourceHost);
                }
                break;
            case MessageKind.LeaveScreen:
                ReleaseAllKeys();
                CancelAllRepeatTimers();
                _cursorHider.OnLeaveScreen(sourceHost);
                break;
            case MessageKind.ScreensaverSync:
                var ss = json.FromSaneJson<ScreensaverSyncMessage>();
                if (ss != null)
                {
                    _log.LogInformation("Screensaver sync from {Host}: active={Active}", sourceHost, ss.Active);
                    if (ss.Active) _screenSaverSync.Activate();
                    else _screenSaverSync.Deactivate();
                }
                break;
            case MessageKind.ClipboardPush:
                var push = json.FromSaneJson<ClipboardPushMessage>();
                if (push != null)
                {
                    _log.LogDebug("Clipboard push from {Host}: text={TextLen}, primary={PrimaryLen}, image={ImageLen}, zip={ZipLen}",
                        sourceHost, push.Text.Length, push.PrimaryText?.Length, push.ImagePng?.Length, push.Zip?.Length);
                    var pushText = !string.IsNullOrEmpty(push.Text) && Encoding.UTF8.GetByteCount(push.Text) <= ClipboardUtils.MaxClipboardBytes ? push.Text : null;
                    var pushPrimary = !string.IsNullOrEmpty(push.PrimaryText) && Encoding.UTF8.GetByteCount(push.PrimaryText) <= ClipboardUtils.MaxClipboardBytes ? push.PrimaryText : null;
                    var pushImage = push.ImagePng?.Length <= ClipboardUtils.MaxClipboardBytes ? push.ImagePng : null;
                    if (pushText == null && !string.IsNullOrEmpty(push.Text))
                        _log.LogWarning("Clipboard push from {Host}: text exceeds {Max} bytes, dropping", sourceHost, ClipboardUtils.MaxClipboardBytes);
                    if (pushPrimary == null && !string.IsNullOrEmpty(push.PrimaryText))
                        _log.LogWarning("Clipboard push from {Host}: primary text exceeds {Max} bytes, dropping", sourceHost, ClipboardUtils.MaxClipboardBytes);
                    if (pushImage == null && push.ImagePng != null)
                        _log.LogWarning("Clipboard push from {Host}: image exceeds {Max} bytes, dropping", sourceHost, ClipboardUtils.MaxClipboardBytes);
                    var pushZip = push.Zip?.Length > 0 ? push.Zip : null;
                    _lastPushed = new ClipboardSnapshot(pushText, pushPrimary, pushImage, pushZip);
                    List<TempFileEntry>? tempFiles = null;
                    if (pushZip != null && _clipboardSync.SupportsFiles)
                    {
                        try { tempFiles = _tempFileManager.ExtractZip(pushZip); }
                        catch (Exception ex) { _log.LogWarning(ex, "Failed to extract clipboard zip from {Host}", sourceHost); }
                    }
                    _clipboardSync.SetClipboard(pushText, pushPrimary, pushImage, tempFiles);
                }
                break;
            case MessageKind.ClipboardPull:
                _log.LogDebug("Clipboard pull from {Host}", sourceHost);
                var text = _clipboardSync.GetText() ?? _lastPushed?.Text;
                var primary = _clipboardSync.GetPrimaryText() ?? _lastPushed?.PrimaryText;
                var image = _clipboardSync.GetImagePng() ?? _lastPushed?.ImagePng;

                // drop fields in priority order until combined size fits
                long textBytes = text != null ? Encoding.UTF8.GetByteCount(text) : 0;
                long primaryBytes = primary != null ? Encoding.UTF8.GetByteCount(primary) : 0;
                long imageBytes = image?.Length ?? 0;
                if (textBytes + primaryBytes + imageBytes > ClipboardUtils.MaxClipboardBytes)
                {
                    _log.LogWarning("Clipboard pull response too large ({Total} bytes), dropping image", textBytes + primaryBytes + imageBytes);
                    image = null; imageBytes = 0;
                }
                if (textBytes + primaryBytes + imageBytes > ClipboardUtils.MaxClipboardBytes)
                {
                    _log.LogWarning("Clipboard pull response still too large ({Total} bytes), dropping primary text", textBytes + primaryBytes);
                    primary = null; primaryBytes = 0;
                }
                if (textBytes + primaryBytes + imageBytes > ClipboardUtils.MaxClipboardBytes)
                {
                    _log.LogWarning("Clipboard pull response still too large ({Total} bytes), dropping text", textBytes);
                    text = null;
                }

                var pullZip = (_clipboardSync.SupportsFiles ? ClipboardUtils.CreateClipboardZip(_clipboardSync.GetFilePaths(), _log) : null) ?? _lastPushed?.Zip;
                long zipBytes = pullZip?.Length ?? 0;
                if (textBytes + primaryBytes + imageBytes + zipBytes > ClipboardUtils.MaxClipboardBytes)
                {
                    _log.LogWarning("Clipboard pull response still too large ({Total} bytes), dropping zip", textBytes + primaryBytes + imageBytes + zipBytes);
                    pullZip = null;
                }
                _log.LogDebug("Pull response: text={TextLen}, primary={PrimaryLen}, image={ImageLen}, zip={ZipLen}", text?.Length, primary?.Length, image?.Length, pullZip?.Length);
                var response = MessageSerializer.Encode(MessageKind.ClipboardPullResponse, new ClipboardPullResponseMessage(text, primary, image, pullZip));
                _ = Send([sourceHost], response).AsTask();
                break;
            default:
                _log.LogDebug("Unhandled message kind {Kind} from {Host}", kind, sourceHost);
                break;
        }
    }

    protected override async Task OnDisconnected()
    {
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
        var platform = DetectLocalPlatform();
        var payload = MessageSerializer.Encode(MessageKind.ScreenInfo, new ScreenInfoMessage(entries, platform));
        _ = Send([masterHost], payload).AsTask();
    }

    private static PeerPlatform DetectLocalPlatform() =>
        OperatingSystem.IsLinux() ? PeerPlatform.Linux :
        OperatingSystem.IsMacOS() ? PeerPlatform.MacOS :
        OperatingSystem.IsWindows() ? PeerPlatform.Windows :
        PeerPlatform.Unknown;
}
