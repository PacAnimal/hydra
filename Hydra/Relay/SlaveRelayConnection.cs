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
    private readonly IHydraProfile _profile;
    private readonly IScreenDetector _screens;
    private readonly IWorldState _peerState;
    private readonly ICursorHider _cursorHider;
    private readonly IScreenSaverSync _screenSaverSync;
    private readonly IClipboardSync _clipboardSync;
    private readonly FileTransferService _fileTransfer;
    private readonly IFileSelectionDetector _selectionDetector;
    private readonly IOsdNotification _osd;
    private readonly IDormancyState _dormancy;

    // keys currently held down on the slave (for release-all on screen leave). access under _keyEventLock,
    // which serialises key handling against the release-all paths run by disconnect/screen-leave handlers.
    private readonly HashSet<(char?, SpecialKey?)> _heldKeys = [];
    private readonly SemaphoreSlim _keyEventLock = new(1, 1);

    // fallback clipboard when Get* returns null because we own the selection (echo suppression)
    private ClipboardSnapshot? _lastPushed;

    // cached screen layout for synchronous mouse move handling (avoids async overhead on the relay hot path)
    private volatile LocalScreenSnapshot? _cachedScreens;

    // set true only after OnAuthenticated completes — guards cursor hide until the slave is fully ready
    private volatile bool _isReady;

    // masters whose cursor is currently on this slave's screen
    // masters whose cursor is currently on this slave's screen. Mutated from SignalR receive handlers AND
    // from OnDisconnected/OnPeers (a different task), so guard it — an unsynchronized HashSet torn between
    // those threads can throw or corrupt. Access only via the helpers below.
    private readonly HashSet<string> _onScreenMasters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _onScreenMastersLock = new();

    private bool IsOnScreenMaster(string host) { lock (_onScreenMastersLock) return _onScreenMasters.Contains(host); }
    private void AddOnScreenMaster(string host) { lock (_onScreenMastersLock) _onScreenMasters.Add(host); }
    private bool RemoveOnScreenMaster(string host) { lock (_onScreenMastersLock) return _onScreenMasters.Remove(host); }
    private void ClearOnScreenMasters() { lock (_onScreenMastersLock) _onScreenMasters.Clear(); }
    private int OnScreenMasterCount { get { lock (_onScreenMastersLock) return _onScreenMasters.Count; } }

    private readonly IActivityTracker _activityTracker;

    // ReSharper disable once ConvertToPrimaryConstructor
#pragma warning disable IDE0290
    public SlaveRelayConnection(IHydraProfile profile, ILogger<RelayConnection> log, IPlatformOutput output, IScreenDetector screens, IWorldState peerState, ICursorHider cursorHider, IScreenSaverSync screenSaverSync, IClipboardSync clipboardSync, FileTransferService fileTransfer, IFileSelectionDetector selectionDetector, IOsdNotification osd, IActivityTracker activityTracker, IDormancyState dormancy)
        : base(profile, log, peerState)
    {
        _output = output;
        _log = log;
        _profile = profile;
        _screens = screens;
        _peerState = peerState;
        _cursorHider = cursorHider;
        _screenSaverSync = screenSaverSync;
        _clipboardSync = clipboardSync;
        _fileTransfer = fileTransfer;
        _selectionDetector = selectionDetector;
        _osd = osd;
        _activityTracker = activityTracker;
        _dormancy = dormancy;

        _screens.ScreensChanged += async snapshot =>
        {
            // sleeping displays enumerate to whatever the OS still lists, usually a single phantom screen.
            // Hold on to the awake snapshot and say nothing; the wake path refreshes and re-announces.
            if (_dormancy.IsDormant)
            {
                _log.LogDebug("Dormant: ignoring screen change to {Count} screen(s)", snapshot.Screens.Count);
                return;
            }
            _cachedScreens = snapshot;
            var masters = await _peerState.GetMasters();
            if (masters.Length > 0)
            {
                _log.LogInformation("Slave screen configuration changed — re-sending screen info");
                foreach (var master in masters)
                    SendScreenInfo(master, snapshot.Entries);
            }
        };

        // the master keeps its cursor parked on us while we sleep, so we never see the KeyUp for whatever
        // is held right now — release it here rather than waking with a stuck modifier.
        _dormancy.Entered += ReleaseAllKeys;

        _dormancy.Exited += async () =>
        {
            var snapshot = await _screens.Get();
            _cachedScreens = snapshot;
            _log.LogInformation("Woke from dormancy — local screens: {Count}", snapshot.Screens.Count);
            foreach (var master in await _peerState.GetMasters())
                SendScreenInfo(master, snapshot.Entries);
        };
    }
#pragma warning restore IDE0290

    // the geometry we advertise to masters: live while awake, the last awake snapshot while dormant. A
    // dormant machine is still a normal, fully-sized peer as far as its masters are concerned — it is
    // refusing input, not shrinking — and a master told otherwise rebuilds its layout around the phantom
    // and drags a parked cursor off us.
    private async ValueTask<LocalScreenSnapshot> AdvertisedScreens()
    {
        if (_dormancy.IsDormant && _cachedScreens is { } lastAwake) return lastAwake;
        var snapshot = await _screens.Get();
        _cachedScreens = snapshot;
        return snapshot;
    }

    protected override async Task OnAuthenticated()
    {
        _isReady = false;
        if (!_output.IsAccessibilityTrusted())
        {
            _log.LogWarning("Output injection permission not granted — open System Settings › Privacy & Security › Accessibility and enable Hydra, then Hydra will continue automatically.");
            await _output.WaitForAccessibilityTrusted(ConnectionToken);
            if (ConnectionToken.IsCancellationRequested) return;
            _log.LogInformation("Accessibility permission granted");
        }
        var snapshot = await AdvertisedScreens();
        _log.LogInformation("Local screens: {Count}", snapshot.Screens.Count);
        _isReady = true;
    }

    protected override async Task OnReceive(string sourceHost, MessageKind kind, ReadOnlyMemory<byte> body)
    {
        if (_dormancy.IsDormant && DropWhileDormant(sourceHost, kind)) return;

        switch (kind)
        {
            case MessageKind.MasterConfig:
                await HandleMasterConfig(sourceHost, body);
                break;
            case MessageKind.MouseMove:
                HandleInputMessage<MouseMoveMessage>(body, kind, sourceHost, move =>
                {
                    if (_profile.DebugMouse)
                        _log.LogInformation("[mouse] move from {Host}: screen={Screen} x={X} y={Y}", sourceHost, move.Screen, move.X, move.Y);
                    MoveToCachedScreen(move.Screen, move.X, move.Y);
                });
                break;
            case MessageKind.KeyEvent:
                {
                    var keyMsg = body.ParseMessage<KeyEventMessage>(_log, kind.ToString());
                    if (keyMsg != null)
                    {
                        if (IsOnScreenMaster(sourceHost))
                            _cursorHider.Show();
                        await HandleKeyEvent(keyMsg);
                    }
                    break;
                }
            case MessageKind.MouseMoveDelta:
                HandleInputMessage<MouseMoveDeltaMessage>(body, kind, sourceHost, delta =>
                {
                    if (_profile.DebugMouse)
                        _log.LogInformation("[mouse] delta from {Host}: dx={Dx} dy={Dy}", sourceHost, delta.Dx, delta.Dy);
                    _output.MoveMouseRelative(delta.Dx, delta.Dy);
                });
                break;
            case MessageKind.MouseButton:
                HandleInputMessage<MouseButtonMessage>(body, kind, sourceHost, _output.InjectMouseButton);
                break;
            case MessageKind.MouseScroll:
                HandleInputMessage<MouseScrollMessage>(body, kind, sourceHost, _output.InjectMouseScroll);
                break;
            case MessageKind.EnterScreen:
                var enter = body.ParseMessage<EnterScreenMessage>(_log, kind.ToString());
                if (enter != null)
                {
                    if (!_dormancy.IsDormant) MoveToCachedScreen(enter.Screen, enter.X, enter.Y);
                    AddOnScreenMaster(sourceHost);
                    _cursorHider.Show();
                }
                break;
            case MessageKind.LeaveScreen:
                await ReleaseAllKeys();
                RemoveOnScreenMaster(sourceHost);
                if (OnScreenMasterCount == 0 && _isReady)
                    _cursorHider.Hide();
                break;
            case MessageKind.ScreensaverSync:
                var ss = body.ParseMessage<ScreensaverSyncMessage>(_log, kind.ToString());
                if (ss != null)
                {
                    _log.LogInformation("Screensaver sync from {Host}: active={Active}", sourceHost, ss.Active);
                    if (ss.Active) _screenSaverSync.Activate();
                    else _screenSaverSync.Deactivate();
                }
                break;
            case MessageKind.ActivityPing:
                _log.LogDebug("Activity ping from {Host} — poking local idle timer", sourceHost);
                await _activityTracker.IncomingPing();
                break;
            case MessageKind.LockScreen:
                {
                    var lockMsg = body.ParseMessage<LockScreenMessage>(_log, kind.ToString());
                    if (lockMsg == null) break;
                    _log.LogInformation("Lock screen request from {Host} (master idle {Ms}ms)", sourceHost, lockMsg.MillisecondsSinceLastInput);
                    var msSinceLocalActivity = _activityTracker.MsSinceLocalActivity;
                    if (msSinceLocalActivity < lockMsg.MillisecondsSinceLastInput)
                    {
                        _log.LogInformation("Skipping lock — local input detected ({Ms:F0}ms ago < {Gap}ms since master input)", msSinceLocalActivity, lockMsg.MillisecondsSinceLastInput);
                        break;
                    }
                    _screenSaverSync.LockScreen();
                    break;
                }
            case MessageKind.ClipboardHash:
                {
                    var hashMsg = body.ParseMessage<ClipboardHashMessage>(_log, kind.ToString());
                    if (hashMsg != null)
                    {
                        var slaveClip = ClipboardUtils.ReadWithFallback(_clipboardSync, _lastPushed, _log, "hash check");
                        if (ClipboardUtils.ClipboardHash(slaveClip) != hashMsg.Hash)
                        {
                            _log.LogDebug("Clipboard hash from {Host}: differs, requesting push", sourceHost);
                            Send([sourceHost], MessageSerializer.Encode(MessageKind.ClipboardPullRequest, new ClipboardPullRequestMessage()));
                        }
                        else
                        {
                            _log.LogDebug("Clipboard hash from {Host}: matches, skipping", sourceHost);
                        }
                    }
                    break;
                }
            case MessageKind.ClipboardPush:
                var push = body.ParseMessage<ClipboardPushMessage>(_log, kind.ToString());
                if (push != null)
                {
                    _log.LogDebug("Clipboard push from {Host}: text={TextLen}, primary={PrimaryLen}, image={ImageLen}",
                        sourceHost, push.Text.Length, push.PrimaryText?.Length, push.ImagePng?.Length);
                    var validated = ClipboardUtils.ValidateFields(push.Text, push.PrimaryText, push.ImagePng, push.Html, push.Rtf, _log, "push", sourceHost);
                    _lastPushed = validated;
                    _clipboardSync.SetClipboard(validated);
                }
                break;
            case MessageKind.ClipboardPull:
                {
                    var pull = body.ParseMessage<ClipboardPullMessage>(_log, kind.ToString());
                    var pullClip = ClipboardUtils.ReadWithFallback(_clipboardSync, _lastPushed, _log, "pull response");
                    if (pull?.MasterHash.HasValue == true && ClipboardUtils.ClipboardHash(pullClip) == pull.MasterHash.Value)
                    {
                        _log.LogDebug("Clipboard pull to {Host}: unchanged, skipping full response", sourceHost);
                        Send([sourceHost], MessageSerializer.Encode(MessageKind.ClipboardPullResponse, new ClipboardPullResponseMessage(null, Unchanged: true)));
                        break;
                    }
                    _log.LogDebug("Clipboard pull to {Host}: text={TextLen}, primary={PrimaryLen}, image={ImageLen}",
                        sourceHost, pullClip.Text?.Length, pullClip.PrimaryText?.Length, pullClip.ImagePng?.Length);
                    Send([sourceHost], MessageSerializer.Encode(MessageKind.ClipboardPullResponse, new ClipboardPullResponseMessage(pullClip.Text, pullClip.PrimaryText, pullClip.ImagePng, Html: pullClip.Html, Rtf: pullClip.Rtf)));
                    break;
                }
            case MessageKind.Osd:
                {
                    var osdMsg = body.ParseMessage<OsdMessage>(_log, kind.ToString());
                    if (osdMsg != null) _osd.Show(osdMsg.Text);
                    break;
                }
            case MessageKind.FileSelectionQuery:
                HandleFileSelectionQuery(sourceHost);
                break;
            case MessageKind.FileStreamRequest:
                await HandleFileStreamRequest(sourceHost, body);
                break;
            case var _ when FileTransferService.IsFileTransferMessage(kind):
                await _fileTransfer.OnMessageAsync(sourceHost, kind, body, this);
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
        ClearOnScreenMasters();
        await ReleaseAllKeys();
        if (masters.Length > 0)
            _cursorHider.Show();
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
        var anyOnScreenMasterLeft = false;
        foreach (var departed in before.Where(h => !afterSet.Contains(h)))
        {
            if (RemoveOnScreenMaster(departed))
                anyOnScreenMasterLeft = true;
            anyMasterLeft = true;
        }
        if (anyOnScreenMasterLeft)
            await ReleaseAllKeys();
        if (anyMasterLeft)
        {
            if (after.Length == 0)
                _cursorHider.Show();
            else if (OnScreenMasterCount == 0 && _isReady)
                _cursorHider.Hide();
        }
        // abort any transfer whose peer has left (e.g. a slave→slave send whose target vanished) so it
        // doesn't stream into the void and falsely report success
        _fileTransfer.AbortIfPeerGone(current, this);
        await base.OnPeers(hostNames);
    }

    // message kinds that drive the local input devices. While dormant these are refused, and their arrival
    // is itself the wake: a master pushing its cursor or keyboard at us is how we get told to come back.
    private static bool IsInjectedInput(MessageKind kind) => kind is
        MessageKind.MouseMove or MessageKind.MouseMoveDelta or MessageKind.MouseButton or
        MessageKind.MouseScroll or MessageKind.KeyEvent or MessageKind.EnterScreen;

    // returns true when the message must not reach the normal handlers. Dormant means we stay on the relay
    // but touch nothing locally: input is refused, and everything else is discarded outright — notably
    // ActivityPing, whose idle-timer poke would light the displays straight back up and defeat dormancy.
    // MasterConfig, LeaveScreen and EnterScreen fall through so peer, held-key and on-screen bookkeeping
    // is still correct once we wake.
    private bool DropWhileDormant(string sourceHost, MessageKind kind)
    {
        if (IsInjectedInput(kind))
        {
            OnInputWhileDormant(sourceHost, kind);
            // EnterScreen still runs: we must know a master is parked on us when we wake, or the local
            // cursor stays hidden under a remote pointer. Only its cursor move is suppressed, in the handler.
            return kind != MessageKind.EnterScreen;
        }
        if (kind is MessageKind.MasterConfig or MessageKind.LeaveScreen) return false;
        _log.LogDebug("Dormant: refused {Kind} from {Host}", kind, sourceHost);
        return true;
    }

    // input keeps arriving for as long as its owner is active, so this doubles as a retry if the first
    // attempt to light the displays didn't take. Only the first one starts the clock — otherwise a master
    // moving its mouse would keep pushing the deadline out and we would never hand the cursor back.
    private void OnInputWhileDormant(string sourceHost, MessageKind kind)
    {
        if (_dormancy.RequestWake())
            _log.LogInformation("Input from {Host} while dormant — restoring displays; {Seconds}s to match the profile or we leave the relay",
                sourceHost, DormancyState.WakeDeadline.TotalSeconds);
        else
            _log.LogDebug("Dormant: refused {Kind} from {Host}", kind, sourceHost);
        WakeDisplay();
    }

    // one attempt per second: input arrives in floods, and the displays need a moment to come back and
    // fire the screen change that has NetworkWatcher re-check the conditions and lift dormancy.
    private const long WakeThrottleMs = 1000;
    private long _lastWakeTicks;

    private void WakeDisplay()
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastWakeTicks);
        if (now - last < WakeThrottleMs) return;
        if (Interlocked.CompareExchange(ref _lastWakeTicks, now, last) != last) return;
        _screenSaverSync.WakeDisplay();
    }

    // parses and dispatches an input event; shows cursor if the master is actively on screen
    private void HandleInputMessage<T>(ReadOnlyMemory<byte> body, MessageKind kind, string sourceHost, Action<T> handler) where T : class
    {
        var msg = body.ParseMessage<T>(_log, kind.ToString());
        if (msg == null) return;
        if (IsOnScreenMaster(sourceHost))
            _cursorHider.Show();
        handler(msg);
    }

    private async Task HandleKeyEvent(KeyEventMessage msg)
    {
        var label = msg.Character.HasValue ? $" '{msg.Character}'" : msg.Key.HasValue ? $" {msg.Key}" : "";
        _log.LogDebug("Key: {Type}{Label} mods={Modifiers}", msg.Type, label, msg.Modifiers);

        // repeats are master-driven: each OS auto-repeat is re-resolved with live modifier/dead-key state on
        // the master and injected here as-is. a repeat implies the key is held on the master, so track it too
        // (Add is idempotent) — this covers a master entering the screen mid-hold, where the slave never saw
        // the initial press and the legacy physical re-press path would otherwise leave the key untracked and
        // stuck on screen leave. injection runs under the lock so it serialises with release-all.
        var heldKey = (msg.Character, msg.Key);
        using (await _keyEventLock.WaitForDisposable())
        {
            if (msg.Type == KeyEventType.KeyUp)
                _heldKeys.Remove(heldKey);
            else
                _heldKeys.Add(heldKey);
            _output.InjectKey(msg);
        }
    }

    private void HandleFileSelectionQuery(string sourceHost)
    {
        if (_fileTransfer.FileTransferOngoing)
        {
            _log.LogInformation("File selection query from {Host} refused: transfer already in progress", sourceHost);
            Send([sourceHost], MessageSerializer.Encode(MessageKind.FileTransferBusy, new FileTransferBusyMessage()));
            return;
        }
        if (!_selectionDetector.IsFileTransferSupported)
        {
            _log.LogInformation("File selection query from {Host}: file transfer not supported on this platform", sourceHost);
            var unsupportedPayload = MessageSerializer.Encode(MessageKind.FileSelectionResponse, new FileSelectionResponseMessage(null, "Action not supported"));
            Send([sourceHost], unsupportedPayload);
            return;
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
    }

    private Task HandleFileStreamRequest(string sourceHost, ReadOnlyMemory<byte> body)
    {
        if (_fileTransfer.FileTransferOngoing)
        {
            _log.LogInformation("Stream request from {Host} refused: transfer already in progress", sourceHost);
            Send([sourceHost], MessageSerializer.Encode(MessageKind.FileTransferBusy, new FileTransferBusyMessage()));
            return Task.CompletedTask;
        }
        var req = body.ParseMessage<FileStreamRequestMessage>(_log, MessageKind.FileStreamRequest.ToString());
        if (req != null)
            _ = _fileTransfer.ExecuteStreamRequest(req.Paths, req.TargetHost, this);
        return Task.CompletedTask;
    }

    // releases every key currently held on the slave — called when a master leaves the screen or disconnects
    // so a key held at that moment does not stay stuck down.
    private async Task ReleaseAllKeys()
    {
        using var _ = await _keyEventLock.WaitForDisposable();
        foreach (var (ch, key) in _heldKeys)
            _output.InjectKey(new KeyEventMessage(KeyEventType.KeyUp, KeyModifiers.None, ch, key));
        _heldKeys.Clear();
    }

    private async Task HandleMasterConfig(string masterHost, ReadOnlyMemory<byte> body)
    {
        var config = body.FromSaneJson<MasterConfigMessage>() ?? new MasterConfigMessage(null);
        var before = await _peerState.GetMasters();
        await _peerState.AddMaster(masterHost, config);
        var after = await _peerState.GetMasters();
        if (after.Length > before.Length && after.Length == 1 && _isReady)
            _cursorHider.Hide();
        var snapshot = await AdvertisedScreens();
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
