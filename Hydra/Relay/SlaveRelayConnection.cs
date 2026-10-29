using System.Threading.Channels;
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
    private readonly ClipboardStreamSender _streams;
    private readonly ClipboardStreamReceiver _receiver;
    private readonly FileTransferService _fileTransfer;
    private readonly IFileSelectionDetector _selectionDetector;
    private readonly IOsdNotification _osd;
    private readonly IDormancyState _dormancy;
    private readonly IWindowFocus _windowFocus;

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

    // on-screen masters whose monitor switch arrived with the cursor hidden where it started (CursorHidden): it stays
    // hidden until their mouse is used (ShowCursorFor), not shown at the centre of the screen for the next keystroke to hide
    private readonly ConcurrentHashSet<string> _hiddenUntilMouse = new([], StringComparer.OrdinalIgnoreCase);

    private readonly IActivityTracker _activityTracker;

    // the one clipboard worker's queue, see OnClipboardWorker
    private readonly Channel<Action> _clipboardWork = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _clipboardWorker;

    // clock times the clipboard stream watchdog: tests alone pass one, since nothing else reaches the receiver, and DI,
    // which registers no TimeProvider, leaves it the system's
    public SlaveRelayConnection(IHydraProfile profile, ILogger<RelayConnection> log, IPlatformOutput output, IScreenDetector screens, IWorldState peerState,
        ICursorHider cursorHider, IScreenSaverSync screenSaverSync, IClipboardSync clipboardSync, FileTransferService fileTransfer,
        IFileSelectionDetector selectionDetector, IOsdNotification osd, IActivityTracker activityTracker, IDormancyState dormancy,
        TimeProvider? clock = null, IWindowFocus? windowFocus = null)
        : base(profile, log, peerState)
    {
        _windowFocus = windowFocus ?? new NullWindowFocus();
        _output = output;
        _screens = screens;
        _cursorHider = cursorHider;
        _screenSaverSync = screenSaverSync;
        _fileTransfer = fileTransfer;
        _selectionDetector = selectionDetector;
        _osd = osd;
        _activityTracker = activityTracker;
        _dormancy = dormancy;
        // a stream pending from the master counts as already applied; the receiver takes pushes, its default
        _clipboard = new ClipboardSlave(clipboardSync, log, PendingFrom);
        _receiver = new ClipboardStreamReceiver(clipboardSync, (host, hash) => _clipboard.Begun(host, hash), _clipboard.Rejected,
            clock ?? TimeProvider.System, log);
        // a slave favours no master, so no host's stream goes first
        _streams = new ClipboardStreamSender(this, () => null, log);
        _receiver.Abandoned += (host, id) => OnStreamGivenUp(host, id, abort: true, failed: false);
        _receiver.Failed += failed => OnStreamGivenUp(failed.Host, failed.Id, abort: !failed.SenderGaveUp, failed: true);
        _clipboardWorker = Task.Run(RunClipboardWorker);

        _screens.ScreensChanged += async snapshot =>
        {
            // sleeping displays enumerate to whatever the OS still lists, usually a single phantom screen.
            // Hold on to the awake snapshot and say nothing; the wake path refreshes and re-announces.
            if (_dormancy.IsDormant)
            {
                if (Log.IsEnabled(LogLevel.Debug))
                    Log.LogDebug("Dormant: ignoring screen change to {Count} {Screens}",
                        snapshot.Screens.Count, Pluralize.Of(snapshot.Screens.Count, "screen"));
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
        _dormancy.Entered += () =>
        {
            DropStreamsForDormancy();
            return ReleaseAll();
        };

        _dormancy.Exited += async () =>
        {
            var snapshot = await _screens.Get(ConnectionToken);
            _cachedScreens = snapshot;
            if (Log.IsEnabled(LogLevel.Information))
                Log.LogInformation("Woke from dormancy — local screens: {Count}", snapshot.Screens.Count);
            AnnounceScreens(await PeerState.GetMasters(), snapshot);
        };
    }

    // by the time _clipboard asks, _receiver is set
    private ulong? PendingFrom(string host) => _receiver.PendingFrom(host);

    // clipboard work reads and writes the os clipboard, which can take seconds, so it runs off the receive path that
    // applies input, on one worker, in arrival order; _clipboard is touched only there
    private void OnClipboardWorker(Action work)
    {
        if (!_clipboardWork.Writer.TryWrite(work) && Log.IsEnabled(LogLevel.Debug))
            Log.LogDebug("Clipboard work dropped: shutting down");
    }

    // clipboard work for a message the receive path took while awake: dormancy starting before it runs, after its
    // streams were dropped, refuses it there as DropWhileDormant would (refused telling the sender: a Begin's master
    // that it failed, a pull's that nothing changed)
    private void OnClipboardWorkerAwake(string sourceHost, MessageKind kind, Action work, Action? refused = null) =>
        OnClipboardWorker(() =>
        {
            if (!_dormancy.IsDormant)
            {
                work();
                return;
            }
            if (Log.IsEnabled(LogLevel.Debug))
                Log.LogDebug("Dormant: refused {Kind} from {Host}", kind, sourceHost);
            refused?.Invoke();
        });

    private async Task RunClipboardWorker()
    {
        await foreach (var work in _clipboardWork.Reader.ReadAllAsync())
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                Log.LogError(ex, "Clipboard work failed");
            }
        }
    }

    // for tests: completes once every clipboard message received so far has been handled
    protected Task DrainClipboardWork()
    {
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_clipboardWork.Writer.TryWrite(idle.SetResult)) idle.SetResult();
        return idle.Task;
    }

    private void HandleHash(string host, ClipboardHashMessage hash)
    {
        if (_clipboard.OnHash(hash, host) != PushNeed.None)
            this.Send(host, MessageKind.ClipboardPullRequest, new ClipboardPullRequestMessage(hash.Visit));
    }

    private void HandlePushNone(string host, ClipboardPushNoneMessage none)
    {
        if (!_clipboard.NoPush(none, host)) return;
        Log.LogWarning("Clipboard push from {Host} will not come; the clipboard stays empty", host);
        _osd.Show(ClipboardStreamReceiver.FailedOsd);
    }

    private void HandlePush(string host, ClipboardPushMessage push)
    {
        if (_clipboard.CopiedSinceHash(host)) return;
        // a whole push is newer than any stream still pending, from that host or another master
        Supersede(host);
        _receiver.Drop(host);
        if (_clipboard.OnPush(push, host)) _osd.Show(ClipboardStreamReceiver.FailedOsd);
    }

    // the master's pull answered
    private void AnswerPull(string host, ClipboardPullMessage pull) =>
        _streams.Send(host, MessageKind.ClipboardPullResponse, _clipboard.OnPull(pull, host));

    // a pull refused, dormant or with sharing off, answered unchanged so it resolves: nothing is offered, and nothing else
    // would tell its master (dormancy dropped every stream both ways)
    private void AnswerPullUnchanged(string host, ClipboardPullMessage pull) =>
        _streams.Send(host, MessageKind.ClipboardPullResponse, new ClipboardPullResponseMessage(pull.PullId, null, Unchanged: true));

    // ... and a Begin told it failed, so its master stops streaming rather than wait for a watchdog
    private void RefuseWhileDormant(string host, ClipboardStreamBeginMessage begin) => this.SendClipboardAbort(host, begin.Id, failed: true);

    private void HandleBegin(string host, ClipboardStreamBeginMessage begin)
    {
        // a Begin older than a copy made here since its hash is told so silently, so its master stops streaming; one from a
        // master another superseded is its newer clipboard, which now starts last and wins
        if (_clipboard.CopiedSinceHash(host))
        {
            this.SendClipboardAbort(host, begin.Id);
            return;
        }
        Supersede(host);
        _clipboard.StreamBegun(host);
        _receiver.OnBegin(host, begin);
    }

    private void HandleChunk(string host, ClipboardStreamChunkMessage chunk)
    {
        // the receiver takes only pushes, so a completed stream is one
        if (HeldOff(host, MessageKind.ClipboardStreamChunk) || _receiver.OnChunk(host, chunk) is not { } completed) return;
        if (completed.Bytes.TryDecodeBody<ClipboardPushMessage>() is { } streamed)
        {
            if (_clipboard.OnPush(streamed, host, streamed: true)) _osd.Show(ClipboardStreamReceiver.FailedOsd);
        }
        else
            _receiver.OnUnreadable(host, chunk.Id);
    }

    // a stream the receiver gave up on: stop its sender if abort, and show a failure if failed
    private void OnStreamGivenUp(string host, long id, bool abort, bool failed)
    {
        if (abort) this.SendClipboardAbort(host, id);
        if (failed) _osd.Show(ClipboardStreamReceiver.FailedOsd);
    }

    // a clipboard from host starts: the last to start wins, so every other master's pending stream is dropped and its
    // sender told, silently (a supersede, no failure); the chunks of those streams are then ignored, while their masters
    // may still start newer ones
    private void Supersede(string host)
    {
        foreach (var stream in _receiver.Supersede(host))
        {
            _clipboard.Superseded(stream.Host);
            this.SendClipboardAbort(stream.Host, stream.Id);
            if (Log.IsEnabled(LogLevel.Debug))
                Log.LogDebug("Clipboard stream {Id} from {Host} superseded by {Winner}'s clipboard", stream.Id, stream.Host, host);
        }
    }

    // whether host's chunk is of a stream another master's superseded: host starting a newer clipboard, or the winner's
    // stream ending, completed or not, lifts it
    private bool HeldOff(string host, MessageKind kind)
    {
        if (_receiver.HeldOffBy(host) is not { } winner) return false;
        if (Log.IsEnabled(LogLevel.Debug))
            Log.LogDebug("Clipboard {Kind} from {Host} ignored: {Winner}'s clipboard is still streaming", kind, host, winner);
        return true;
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
            Log.LogWarning("Output injection permission not granted — open System Settings › Privacy & Security › Accessibility and enable Hydra, "
                + "then Hydra will continue automatically.");
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
        if (_dormancy.IsDormant && DropWhileDormant(sourceHost, kind, body)) return;

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
                        if (!_dormancy.IsDormant)
                        {
                            MoveToCachedScreen(enter.Screen, enter.X, enter.Y);
                            if (enter.Focus) FocusOnScreen(enter.Screen);
                        }
                        _onScreenMasters.Add(sourceHost);
                        OnClipboardWorker(() => _clipboard.Entered(sourceHost));
                        if (enter.CursorHidden)
                        {
                            _hiddenUntilMouse.Add(sourceHost);
                            _cursorHider.Hide();
                        }
                        else
                        {
                            _hiddenUntilMouse.Remove(sourceHost);
                            _cursorHider.Show();
                        }
                    }
                    break;
                }
            case MessageKind.LeaveScreen:
                await ReleaseAll();
                _onScreenMasters.Remove(sourceHost);
                _hiddenUntilMouse.Remove(sourceHost);
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
                    if (body.ParseMessage<ClipboardHashMessage>(Log, sourceHost) is { } hashMsg)
                        OnClipboardWorkerAwake(sourceHost, kind, () => HandleHash(sourceHost, hashMsg));
                    break;
                }
            case MessageKind.ClipboardPushNone:
                {
                    if (body.ParseMessage<ClipboardPushNoneMessage>(Log, sourceHost) is { } none)
                        OnClipboardWorkerAwake(sourceHost, kind, () => HandlePushNone(sourceHost, none));
                    break;
                }
            case MessageKind.ClipboardPush:
                {
                    if (body.ParseMessage<ClipboardPushMessage>(Log, sourceHost) is { } push)
                        OnClipboardWorkerAwake(sourceHost, kind, () => HandlePush(sourceHost, push));
                    break;
                }
            case MessageKind.ClipboardPull:
                {
                    if (body.ParseMessage<ClipboardPullMessage>(Log, sourceHost) is { } pull)
                        OnClipboardWorkerAwake(sourceHost, kind, () => AnswerPull(sourceHost, pull), () => AnswerPullUnchanged(sourceHost, pull));
                    break;
                }
            case MessageKind.ClipboardStreamBegin:
                {
                    if (body.ParseMessage<ClipboardStreamBeginMessage>(Log, sourceHost) is { } begin)
                        OnClipboardWorkerAwake(sourceHost, kind, () => HandleBegin(sourceHost, begin), () => RefuseWhileDormant(sourceHost, begin));
                    break;
                }
            case MessageKind.ClipboardStreamChunk:
                {
                    if (body.ParseMessage<ClipboardStreamChunkMessage>(Log, sourceHost) is { } chunk)
                        OnClipboardWorkerAwake(sourceHost, kind, () => HandleChunk(sourceHost, chunk));
                    break;
                }
            case MessageKind.ClipboardStreamAbort:
                {
                    if (body.ParseMessage<ClipboardStreamAbortMessage>(Log, sourceHost) is not { } abort) break;
                    // each side's ids are its own, so each ignores one it doesn't hold
                    OnClipboardWorkerAwake(sourceHost, kind, () =>
                    {
                        _receiver.OnAbort(sourceHost, abort);
                        _streams.OnAbort(sourceHost, abort);
                    });
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
        // after the clipboard work already queued, so a stream it begins is dropped too
        OnClipboardWorker(() =>
        {
            TellDropped(_receiver.DropAll(), "the relay disconnected", show: true);
            _streams.DropAll();
        });
        var masters = await PeerState.GetMasters();
        _onScreenMasters.Clear();
        _hiddenUntilMouse.Clear();
        await ReleaseAll();
        if (masters.Length > 0)
            _cursorHider.Show();
        await PeerState.PruneMasters([]);
        Log.LogWarning("Relay connection lost — cursor restored on slave");
    }

    protected override async Task OnShutdown(CancellationToken cancel)
    {
        // the worker finishes what is queued, unless a clipboard read outlasts the shutdown
        _clipboardWork.Writer.TryComplete();
        try
        {
            await _clipboardWorker.WaitAsync(cancel);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            Log.LogWarning("Clipboard work still running at shutdown");
        }
        _receiver.Dispose();
        await _streams.DisposeAsync();
        await base.OnShutdown(cancel);
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
        OnClipboardWorker(() =>
        {
            TellDropped(_receiver.Retain(current), "it left the relay", show: true, present: current);
            _streams.Retain(current);
        });
        await base.OnPeers(hostNames);
    }

    // a push this slave was waiting for that will never land (a stream dropped, or one a hash emptied the clipboard for
    // from a master not in present, every master when null): an honest empty, logged, and shown once unless the screen is dark
    private void TellDropped(IReadOnlyList<DroppedClipboardStream> dropped, string why, bool show, IReadOnlySet<string>? present = null)
    {
        var streams = ClipboardStreamReceiver.LogDropped(dropped, why, Log);
        var awaited = _clipboard.ForgetAwaitedPushes(why, present);
        if ((streams || awaited) && show) _osd.Show(ClipboardStreamReceiver.FailedOsd);
    }

    // dormancy drops streams both ways at once, so nothing pends until a watchdog fires an osd on a dark screen; awake again
    // before the drop runs, it drops nothing, since a stream begun since is the wake's (one from before keeps its watchdog)
    private void DropStreamsForDormancy() =>
        OnClipboardWorker(() =>
        {
            if (!_dormancy.IsDormant)
            {
                if (Log.IsEnabled(LogLevel.Debug))
                    Log.LogDebug("Dormancy over before its clipboard streams were dropped; none dropped");
                return;
            }
            TellDropped(_receiver.DropAll(), "the displays went dormant", show: false);
            _streams.AbortAll();
        });

    // messages that mean a human is back, ActivityPing included (a master sends it only on real local input)
    private static bool IsWakeSignal(MessageKind kind) => kind is
        MessageKind.MouseMove or MessageKind.MouseMoveDelta or MessageKind.MouseButton or
        MessageKind.MouseScroll or MessageKind.KeyEvent or MessageKind.KeyEventBatch or
        MessageKind.EnterScreen or MessageKind.ActivityPing;

    // true when the message must not reach its handler; what falls through keeps the bookkeeping right for the wake
    private bool DropWhileDormant(string sourceHost, MessageKind kind, ReadOnlyMemory<byte> body)
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
        if (kind == MessageKind.ClipboardStreamBegin && body.ParseMessage<ClipboardStreamBeginMessage>(Log, sourceHost) is { } begin)
            RefuseWhileDormant(sourceHost, begin);
        if (kind == MessageKind.ClipboardPull && body.ParseMessage<ClipboardPullMessage>(Log, sourceHost) is { } pull)
            AnswerPullUnchanged(sourceHost, pull);
        return true;
    }

    // clipboardSync Off, for a master that does not read SharingOff in our screen info: what it waits on is answered so it
    // stops waiting (a pull unchanged, a Begin aborted silently, a file request aborted), the rest dropped
    private bool RefuseWhileSharingOff(string sourceHost, MessageKind kind, ReadOnlyMemory<byte> body)
    {
        switch (kind)
        {
            case MessageKind.ClipboardPull when body.ParseMessage<ClipboardPullMessage>(Log, sourceHost) is { } pull:
                AnswerPullUnchanged(sourceHost, pull);
                return true;
            case MessageKind.ClipboardStreamBegin when body.ParseMessage<ClipboardStreamBeginMessage>(Log, sourceHost) is { } begin:
                this.SendClipboardAbort(sourceHost, begin.Id);
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
        ShowCursorFor(sourceHost, mouse: true);
        handler(msg);
    }

    // input from a master on screen shows the cursor; after a monitor switch, only its mouse does (_hiddenUntilMouse)
    private void ShowCursorFor(string sourceHost, bool mouse)
    {
        if (!_onScreenMasters.Contains(sourceHost)) return;
        if (mouse) _hiddenUntilMouse.Remove(sourceHost);
        else if (_hiddenUntilMouse.Contains(sourceHost)) return;
        _cursorHider.Show();
    }

    // applies key events in order; shows cursor if the master is actively on screen
    private async Task ApplyKeyEvents(string sourceHost, KeyEventMessage[] events)
    {
        ShowCursorFor(sourceHost, mouse: false);
        foreach (var keyEvent in events)
            await HandleKeyEvent(keyEvent);
    }

    // tracked like a key, so a release-all lets go of it
    private async Task HandleMouseButton(string sourceHost, MouseButtonMessage msg)
    {
        ShowCursorFor(sourceHost, mouse: true);
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
        _output.RestoreLocks();
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

    // the monitor switch hotkey arriving: the window last worked in on that screen comes forward
    private void FocusOnScreen(string screenName)
    {
        if (_cachedScreens?.Screens.FirstOrDefault(s => s.Name.EqualsIgnoreCase(screenName)) is { } screen)
            _windowFocus.FocusFrontmostOn(screen.Bounds);
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
