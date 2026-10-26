using Cathedral.Extensions;
using Cathedral.Utils;
using Hydra.Config;
using Hydra.FileTransfer;
using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Platform;
using Hydra.Screen;
using Microsoft.Extensions.Logging;

namespace Hydra.Relay;

public class SlaveRelayConnection : RelayConnection
{
    private readonly IPlatformOutput _output;
    private readonly IScreenDetector _screens;
    private readonly ICursorHider _cursorHider;
    private readonly IScreenSaverSync _screenSaverSync;
    private readonly ClipboardSlave _clipboard;
    private readonly FileTransferService _fileTransfer;
    private readonly IFileSelectionDetector _selectionDetector;
    private readonly IOsdNotification _osd;
    private readonly IDormancyState _dormancy;

    // keys and buttons currently held down on the slave (for release-all on screen leave). access under _heldLock,
    // which serialises their injection against the release-all paths run by disconnect/screen-leave handlers.
    private readonly HashSet<(char?, SpecialKey?)> _heldKeys = [];
    private readonly HashSet<MouseButton> _heldButtons = [];
    private readonly SemaphoreSlim _heldLock = new(1, 1);

    // one selection query at a time: two would race their answers to the master, which keeps whichever lands last
    private readonly SemaphoreSlim _selectionQuery = new(1, 1);
    // who the running query answers; kept after it ends, since only the next holder of _selectionQuery replaces it
    private volatile SelectionQueryOrigin? _selectionQueryOrigin;

    // cached screen layout for synchronous mouse move handling (avoids async overhead on the relay hot path)
    private volatile LocalScreenSnapshot? _cachedScreens;

    // set true only after OnAuthenticated completes — guards cursor hide until the slave is fully ready
    private volatile bool _isReady;

    // masters whose cursor is currently on this slave's screen; receive handlers and OnDisconnected/OnPeers both change it
    private readonly ConcurrentHashSet<string> _onScreenMasters = new([], StringComparer.OrdinalIgnoreCase);

    private readonly IActivityTracker _activityTracker;

    public SlaveRelayConnection(IHydraProfile profile, ILogger<RelayConnection> log, IPlatformOutput output, IScreenDetector screens, IWorldState peerState, ICursorHider cursorHider, IScreenSaverSync screenSaverSync, IClipboardSync clipboardSync, FileTransferService fileTransfer, IFileSelectionDetector selectionDetector, IOsdNotification osd, IActivityTracker activityTracker, IDormancyState dormancy)
        : base(profile, log, peerState)
    {
        _output = output;
        _screens = screens;
        _cursorHider = cursorHider;
        _screenSaverSync = screenSaverSync;
        _clipboard = new ClipboardSlave(clipboardSync, log);
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
                if (Log.IsEnabled(LogLevel.Debug))
                    Log.LogDebug("Dormant: ignoring screen change to {Count} {Screens}", snapshot.Screens.Count, Pluralize.Of(snapshot.Screens.Count, "screen"));
                return;
            }
            _cachedScreens = snapshot;
            var masters = await PeerState.GetMasters();
            if (masters.Length > 0)
            {
                Log.LogInformation("Slave screen configuration changed — re-sending screen info");
                AnnounceScreens(masters, snapshot);
            }
        };

        // the master keeps its cursor parked on us while we sleep, so we never see the up for whatever
        // is held right now — release it here rather than waking with a stuck modifier or button.
        _dormancy.Entered += ReleaseAll;

        _dormancy.Exited += async () =>
        {
            var snapshot = await _screens.Get(ConnectionToken);
            _cachedScreens = snapshot;
            if (Log.IsEnabled(LogLevel.Information))
                Log.LogInformation("Woke from dormancy — local screens: {Count}", snapshot.Screens.Count);
            AnnounceScreens(await PeerState.GetMasters(), snapshot);
        };
    }

    private void AnnounceScreens(string[] masters, LocalScreenSnapshot snapshot)
    {
        foreach (var master in masters)
            SendScreenInfo(master, snapshot.Entries);
    }

    // live while awake, the last awake snapshot while dormant: dormant means refusing input, not shrinking
    private async ValueTask<LocalScreenSnapshot> AdvertisedScreens()
    {
        if (_dormancy.IsDormant && _cachedScreens is { } lastAwake) return lastAwake;

        // connection token: a wedged detector must not hold the login
        var snapshot = await _screens.Get(ConnectionToken);
        _cachedScreens = snapshot;
        return snapshot;
    }

    protected override async Task OnAuthenticated()
    {
        _isReady = false;
        if (!_output.IsAccessibilityTrusted())
        {
            Log.LogWarning("Output injection permission not granted — open System Settings › Privacy & Security › Accessibility and enable Hydra, then Hydra will continue automatically.");
            await _output.WaitForAccessibilityTrusted(ConnectionToken);
            if (ConnectionToken.IsCancellationRequested) return;
            Log.LogInformation("Accessibility permission granted");
        }
        var snapshot = await AdvertisedScreens();
        if (Log.IsEnabled(LogLevel.Information))
            Log.LogInformation("Local screens: {Count}", snapshot.Screens.Count);
        _isReady = true;
    }

    protected override async Task OnReceive(string sourceHost, MessageKind kind, ReadOnlyMemory<byte> body)
    {
        // sharing off first, so a dormant slave refuses as itself rather than as dormant (it claims no wake signal)
        if (Profile.SharingOff && RefuseWhileSharingOff(sourceHost, kind, body)) return;
        if (_dormancy.IsDormant && DropWhileDormant(sourceHost, kind)) return;

        switch (kind)
        {
            case MessageKind.MasterConfig:
                await HandleMasterConfig(sourceHost, body);
                break;
            case MessageKind.MouseMove:
                HandleInputMessage<MouseMoveMessage>(body, sourceHost, move =>
                {
                    if (Profile.DebugMouse && Log.IsEnabled(LogLevel.Information))
                        Log.LogInformation("[mouse] move from {Host}: screen={Screen} x={X} y={Y}", sourceHost, move.Screen, move.X, move.Y);
                    MoveToCachedScreen(move.Screen, move.X, move.Y);
                });
                break;
            case MessageKind.KeyEvent:
                {
                    var keyMsg = body.ParseMessage<KeyEventMessage>(Log, sourceHost);
                    if (keyMsg != null)
                        await ApplyKeyEvents(sourceHost, [keyMsg]);
                    break;
                }
            case MessageKind.KeyEventBatch:
                {
                    // in order, every one: a partial application leaves a key down
                    var batch = body.ParseMessage<KeyEventBatchMessage>(Log, sourceHost);
                    if (batch is { Events.Length: > 0 })
                        await ApplyKeyEvents(sourceHost, batch.Events);
                    break;
                }
            case MessageKind.MouseMoveDelta:
                HandleInputMessage<MouseMoveDeltaMessage>(body, sourceHost, delta =>
                {
                    if (Profile.DebugMouse && Log.IsEnabled(LogLevel.Information))
                        Log.LogInformation("[mouse] delta from {Host}: dx={Dx} dy={Dy}", sourceHost, delta.Dx, delta.Dy);
                    _output.MoveMouseRelative(delta.Dx, delta.Dy);
                });
                break;
            case MessageKind.MouseButton:
                {
                    var button = body.ParseMessage<MouseButtonMessage>(Log, sourceHost);
                    if (button != null)
                        await HandleMouseButton(sourceHost, button);
                    break;
                }
            case MessageKind.MouseScroll:
                HandleInputMessage<MouseScrollMessage>(body, sourceHost, _output.InjectMouseScroll);
                break;
            case MessageKind.EnterScreen:
                {
                    var enter = body.ParseMessage<EnterScreenMessage>(Log, sourceHost);
                    if (enter != null)
                    {
                        if (!_dormancy.IsDormant) MoveToCachedScreen(enter.Screen, enter.X, enter.Y);
                        _onScreenMasters.Add(sourceHost);
                        _cursorHider.Show();
                    }
                    break;
                }
            case MessageKind.LeaveScreen:
                await ReleaseAll();
                _onScreenMasters.Remove(sourceHost);
                if (!_onScreenMasters.Any() && _isReady)
                    _cursorHider.Hide();
                break;
            case MessageKind.ScreensaverSync:
                {
                    var ss = body.ParseMessage<ScreensaverSyncMessage>(Log, sourceHost);
                    if (ss != null)
                    {
                        if (Log.IsEnabled(LogLevel.Information))
                            Log.LogInformation("Screensaver sync from {Host}: active={Active}", sourceHost, ss.Active);
                        if (ss.Active) _screenSaverSync.Activate();
                        else _screenSaverSync.Deactivate();
                    }
                    break;
                }
            case MessageKind.ActivityPing:
                if (Log.IsEnabled(LogLevel.Debug))
                    Log.LogDebug("Activity ping from {Host} — poking local idle timer", sourceHost);
                await _activityTracker.IncomingPing();
                break;
            case MessageKind.LockScreen:
                {
                    var lockMsg = body.ParseMessage<LockScreenMessage>(Log, sourceHost);
                    if (lockMsg == null) break;
                    if (Log.IsEnabled(LogLevel.Information))
                        Log.LogInformation("Lock screen request from {Host} (master idle {Ms}ms)", sourceHost, lockMsg.MillisecondsSinceLastInput);
                    var msSinceLocalActivity = _activityTracker.MsSinceLocalActivity;
                    if (msSinceLocalActivity < lockMsg.MillisecondsSinceLastInput)
                    {
                        if (Log.IsEnabled(LogLevel.Information))
                            Log.LogInformation("Skipping lock — local input detected ({Ms:F0}ms ago < {Gap}ms since master input)",
                                msSinceLocalActivity, lockMsg.MillisecondsSinceLastInput);
                        break;
                    }
                    _screenSaverSync.LockScreen();
                    break;
                }
            case MessageKind.ClipboardHash:
                {
                    var hashMsg = body.ParseMessage<ClipboardHashMessage>(Log, sourceHost);
                    if (hashMsg != null && _clipboard.OnHash(hashMsg, sourceHost) != PushNeed.None)
                        this.Send(sourceHost, MessageKind.ClipboardPullRequest, new ClipboardPullRequestMessage());
                    break;
                }
            case MessageKind.ClipboardPush:
                {
                    var push = body.ParseMessage<ClipboardPushMessage>(Log, sourceHost);
                    if (push != null)
                        _clipboard.OnPush(push, sourceHost);
                    break;
                }
            case MessageKind.ClipboardPull:
                {
                    var pull = body.ParseMessage<ClipboardPullMessage>(Log, sourceHost);
                    if (pull != null)
                        this.Send(sourceHost, MessageKind.ClipboardPullResponse, _clipboard.OnPull(pull, sourceHost));
                    break;
                }
            case MessageKind.Osd:
                {
                    var osdMsg = body.ParseMessage<OsdMessage>(Log, sourceHost);
                    if (osdMsg != null) _osd.Show(osdMsg.Text);
                    break;
                }
            case MessageKind.FileSelectionQuery:
                {
                    var connection = ConnectionToken;
                    if (!_selectionQuery.Wait(0))
                    {
                        if (Log.IsEnabled(LogLevel.Information))
                            Log.LogInformation("File selection query from {Host} dropped: the previous one is still running", sourceHost);
                        // the master takes its host's first answer, so a double press must leave that to the running query
                        if (_selectionQueryOrigin?.Is(sourceHost, connection) != true)
                            AnswerSelectionQuery(sourceHost, FileSelectionResult.InProgressMessage);
                        break;
                    }
                    _selectionQueryOrigin = new SelectionQueryOrigin(sourceHost, connection);
                    Background.RunTask(() =>
                    {
                        HandleFileSelectionQuery(sourceHost, connection);
                        return Task.CompletedTask;
                    }, Log);
                    break;
                }
            case MessageKind.FileStreamRequest:
                await HandleFileStreamRequest(sourceHost, body);
                break;
            case var _ when FileTransferService.IsFileTransferMessage(kind):
                if (await _fileTransfer.OnMessage(sourceHost, kind, body, this))
                    _osd.Show(FileTransferService.PastedOsd);
                break;
            default:
                await base.OnReceive(sourceHost, kind, body);
                break;
        }
    }

    protected override async Task OnDisconnected()
    {
        _fileTransfer.Abort(this, "relay disconnected");
        var masters = await PeerState.GetMasters();
        _onScreenMasters.Clear();
        await ReleaseAll();
        if (masters.Length > 0)
            _cursorHider.Show();
        await PeerState.PruneMasters([]);
        Log.LogWarning("Relay connection lost — cursor restored on slave");
    }

    protected override async Task OnPeers(string[] hostNames)
    {
        var current = new HashSet<string>(hostNames, StringComparer.OrdinalIgnoreCase);
        var before = await PeerState.GetMasters();
        await PeerState.PruneMasters(current);
        var after = await PeerState.GetMasters();
        var afterSet = new HashSet<string>(after, StringComparer.OrdinalIgnoreCase);
        var anyMasterLeft = false;
        var anyOnScreenMasterLeft = false;
        foreach (var departed in before.Where(h => !afterSet.Contains(h)))
        {
            if (_onScreenMasters.Remove(departed))
                anyOnScreenMasterLeft = true;
            anyMasterLeft = true;
        }
        if (anyOnScreenMasterLeft)
            await ReleaseAll();
        if (anyMasterLeft)
        {
            if (after.Length == 0)
                _cursorHider.Show();
            else if (!_onScreenMasters.Any() && _isReady)
                _cursorHider.Hide();
        }
        // abort any transfer whose peer has left (e.g. a slave→slave send whose target vanished) so it
        // doesn't stream into the void and falsely report success
        _fileTransfer.AbortIfPeerGone(current, this);
        await base.OnPeers(hostNames);
    }

    // messages that mean a human is back, ActivityPing included (a master sends it only on real local input)
    private static bool IsWakeSignal(MessageKind kind) => kind is
        MessageKind.MouseMove or MessageKind.MouseMoveDelta or MessageKind.MouseButton or
        MessageKind.MouseScroll or MessageKind.KeyEvent or MessageKind.KeyEventBatch or
        MessageKind.EnterScreen or MessageKind.ActivityPing;

    // true when the message must not reach its handler; what falls through keeps the bookkeeping right for the wake
    private bool DropWhileDormant(string sourceHost, MessageKind kind)
    {
        if (IsWakeSignal(kind))
        {
            OnActivityWhileDormant(sourceHost, kind);
            // EnterScreen still runs, minus its cursor move, so we know a master is parked on us when we wake
            return kind != MessageKind.EnterScreen;
        }
        if (kind is MessageKind.MasterConfig or MessageKind.LeaveScreen
            or MessageKind.LatencyProbe or MessageKind.LatencyProbeResponse
            or MessageKind.RemoteManagementRequest or MessageKind.RemoteManagementResponse) return false;
        if (Log.IsEnabled(LogLevel.Debug))
            Log.LogDebug("Dormant: refused {Kind} from {Host}", kind, sourceHost);
        if (kind == MessageKind.FileSelectionQuery)
            AnswerSelectionQuery(sourceHost, FileSelectionResult.UnavailableMessage);
        return true;
    }

    // clipboardSync Off, for a master that does not read SharingOff in our screen info: what it waits on is answered so it
    // stops waiting (a pull unchanged, a file request aborted), the rest dropped
    private bool RefuseWhileSharingOff(string sourceHost, MessageKind kind, ReadOnlyMemory<byte> body)
    {
        switch (kind)
        {
            case MessageKind.ClipboardPull when body.ParseMessage<ClipboardPullMessage>(Log, sourceHost) is { } pull:
                this.Send(sourceHost, MessageKind.ClipboardPullResponse, new ClipboardPullResponseMessage(pull.PullId, null, Unchanged: true));
                return true;
            case MessageKind.FileSelectionQuery:
                AnswerSelectionQuery(sourceHost, FileTransferService.SharingOffOsd);
                return true;
            case MessageKind.FileTransferRequest:
                SendSharingOffAbort(sourceHost);
                return true;
            case MessageKind.FileStreamRequest:
                {
                    SendSharingOffAbort(sourceHost);
                    // a target another slave set up for our stream waits on it, so it is told too
                    if (body.ParseMessage<FileStreamRequestMessage>(Log, sourceHost) is { } req && !req.TargetHost.EqualsIgnoreCase(sourceHost))
                        SendSharingOffAbort(req.TargetHost);
                    return true;
                }
            default:
                return MessageKinds.IsClipboard(kind) || FileTransferService.IsFileTransferMessage(kind);
        }
    }

    private void SendSharingOffAbort(string host) =>
        this.Send(host, MessageKind.FileTransferAbort, new FileTransferAbortMessage(FileTransferService.ReasonSharingOff));

    // the master keeps its copy pending until the query is answered, so one we drop still gets a reply
    private void AnswerSelectionQuery(string sourceHost, string message) =>
        this.Send(sourceHost, MessageKind.FileSelectionResponse, new FileSelectionResponseMessage(null, message));

    // every activity retries the wake, but only the first starts the deadline, or input would keep pushing it out
    private void OnActivityWhileDormant(string sourceHost, MessageKind kind)
    {
        if (_dormancy.RequestWake())
        {
            if (Log.IsEnabled(LogLevel.Information))
                Log.LogInformation("Activity from {Host} while dormant — restoring displays; {Seconds}s to match the profile or we leave the relay",
                    sourceHost, DormancyState.WakeDeadline.TotalSeconds);
        }
        else if (Log.IsEnabled(LogLevel.Debug))
            Log.LogDebug("Dormant: refused {Kind} from {Host}", kind, sourceHost);
        WakeDisplay();
    }

    // input arrives in floods, and the displays need a moment to come back
    private static readonly TimeSpan WakeInterval = TimeSpan.FromSeconds(1);
    private readonly IntervalGate _wake = new(WakeInterval);

    private void WakeDisplay()
    {
        if (_wake.TryEnter())
            _screenSaverSync.WakeDisplay();
    }

    // parses and dispatches an input event; shows cursor if the master is actively on screen
    private void HandleInputMessage<T>(ReadOnlyMemory<byte> body, string sourceHost, Action<T> handler) where T : class
    {
        var msg = body.ParseMessage<T>(Log, sourceHost);
        if (msg == null) return;
        ShowCursorFor(sourceHost);
        handler(msg);
    }

    private void ShowCursorFor(string sourceHost)
    {
        if (_onScreenMasters.Contains(sourceHost))
            _cursorHider.Show();
    }

    // applies key events in order; shows cursor if the master is actively on screen
    private async Task ApplyKeyEvents(string sourceHost, KeyEventMessage[] events)
    {
        ShowCursorFor(sourceHost);
        foreach (var keyEvent in events)
            await HandleKeyEvent(keyEvent);
    }

    // tracked like a key, so a release-all lets go of it
    private async Task HandleMouseButton(string sourceHost, MouseButtonMessage msg)
    {
        ShowCursorFor(sourceHost);
        using (await _heldLock.WaitForDisposable())
        {
            if (msg.IsPressed)
                _heldButtons.Add(msg.Button);
            else
                _heldButtons.Remove(msg.Button);
            _output.InjectMouseButton(msg);
        }
    }

    private async Task HandleKeyEvent(KeyEventMessage msg)
    {
        var label = msg.Character.HasValue ? $" '{msg.Character}'" : msg.Key.HasValue ? $" {msg.Key}" : "";
        if (Log.IsEnabled(LogLevel.Debug))
            Log.LogDebug("Key: {Type}{Label} mods={Modifiers}", msg.Type, label, msg.Modifiers);

        // a repeat is injected as-is and leaves the key held (Add is idempotent); injecting under the lock serialises
        // with release-all
        var heldKey = (msg.Character, msg.Key);
        using (await _heldLock.WaitForDisposable())
        {
            if (msg.Type == KeyEventType.KeyUp)
                _heldKeys.Remove(heldKey);
            else
                _heldKeys.Add(heldKey);
            _output.InjectKey(msg);
        }
    }

    // answers once the query is released, so a query the master sends on seeing the answer is never dropped
    private void HandleFileSelectionQuery(string sourceHost, CancellationToken connection)
    {
        byte[] response;
        try
        {
            response = QueryFileSelection(sourceHost, connection);
        }
        catch (OperationCanceledException) when (connection.IsCancellationRequested)
        {
            if (Log.IsEnabled(LogLevel.Information))
                Log.LogInformation("File selection query from {Host} abandoned: its connection dropped", sourceHost);
            return;
        }
        finally
        {
            _selectionQuery.Release();
        }
        // a query outliving its connection must not answer into the next one
        if (!connection.IsCancellationRequested)
            Send([sourceHost], response);
    }

    private byte[] QueryFileSelection(string sourceHost, CancellationToken connection)
    {
        if (_fileTransfer.FileTransferOngoing)
        {
            if (Log.IsEnabled(LogLevel.Information))
                Log.LogInformation("File selection query from {Host} refused: transfer already in progress", sourceHost);
            return MessageSerializer.Encode(MessageKind.FileTransferBusy, new FileTransferBusyMessage());
        }
        if (!_selectionDetector.IsFileTransferSupported)
        {
            if (Log.IsEnabled(LogLevel.Information))
                Log.LogInformation("File selection query from {Host}: file transfer not supported on this platform", sourceHost);
            return MessageSerializer.Encode(MessageKind.FileSelectionResponse, new FileSelectionResponseMessage(null, FileTransferService.NotSupportedOsd));
        }
        FileSelectionResult result;
        try
        {
            result = _selectionDetector.GetSelectedPaths(connection);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.LogError(ex, "File selection query from {Host} failed", sourceHost);
            result = FileSelectionResult.Failure;
        }
        result.Log(Log, $"File selection query from {sourceHost}", _selectionDetector.FileManagerName);
        return MessageSerializer.Encode(MessageKind.FileSelectionResponse, result.ToResponse(_selectionDetector.FileManagerName));
    }

    private Task HandleFileStreamRequest(string sourceHost, ReadOnlyMemory<byte> body)
    {
        if (_fileTransfer.FileTransferOngoing)
        {
            if (Log.IsEnabled(LogLevel.Information))
                Log.LogInformation("Stream request from {Host} refused: transfer already in progress", sourceHost);
            this.Send(sourceHost, MessageKind.FileTransferBusy, new FileTransferBusyMessage());
            return Task.CompletedTask;
        }
        var req = body.ParseMessage<FileStreamRequestMessage>(Log, sourceHost);
        if (req != null)
            _ = _fileTransfer.ExecuteStreamRequest(req.Paths, req.TargetHost, this);
        return Task.CompletedTask;
    }

    // releases every key and button currently held on the slave — called when a master leaves the screen or
    // disconnects, or we go dormant, so one held at that moment does not stay stuck down.
    private async Task ReleaseAll()
    {
        using var _ = await _heldLock.WaitForDisposable();
        foreach (var (ch, key) in _heldKeys)
            _output.InjectKey(new KeyEventMessage(KeyEventType.KeyUp, KeyModifiers.None, ch, key));
        _heldKeys.Clear();
        foreach (var button in _heldButtons)
            _output.InjectMouseButton(new MouseButtonMessage(button, false));
        _heldButtons.Clear();
    }

    private async Task HandleMasterConfig(string masterHost, ReadOnlyMemory<byte> body)
    {
        if (body.ParseMessage<MasterConfigMessage>(Log, masterHost) is not { } config) return;
        var before = await PeerState.GetMasters();
        await PeerState.AddMaster(masterHost, config);
        var after = await PeerState.GetMasters();
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
        if (Log.IsEnabled(LogLevel.Information))
            Log.LogInformation("Sending screen info to {Master}: {Count} {Screens}", masterHost, entries.Count, Pluralize.Of(entries.Count, "screen"));
        this.Send(masterHost, MessageKind.ScreenInfo, new ScreenInfoMessage(entries, PeerPlatforms.Local, Profile.SharingOff));
    }

    private sealed record SelectionQueryOrigin(string Host, CancellationToken Connection)
    {
        public bool Is(string host, CancellationToken connection) => Host.EqualsIgnoreCase(host) && Connection == connection;
    }
}
