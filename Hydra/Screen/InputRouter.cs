using System.Text.Json;
using System.Threading.Channels;
using Cathedral.Extensions;
using Cathedral.Utils;
using Hydra.Config;
using Hydra.FileTransfer;
using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Platform;
using Hydra.Relay;
using Hydra.Management;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hydra.Screen;

public class InputRouter(
    IPlatformInput platform,
    ICursorHider cursorHider,
    IHydraProfile profile,
    IRelaySender relay,
    IScreenDetector screens,
    ILoggerFactory loggerFactory,
    ILogger<InputRouter> log,
    IScreenSaverSync screenSaverSync,
    IClipboardSync clipboardSync,
    FileTransferService fileTransfer,
    IFileSelectionDetector selectionDetector,
    IOsdNotification osd,
    IActivityTracker activityTracker,
    IWorldState? peerState = null,
    Func<long>? getTickCount = null,
    PeerPlatform? localPlatform = null,
    TimeProvider? timeProvider = null)
    : IHostedService
{
    // How often a mouse position is sent to a slave, and therefore how long a batch of raw samples is
    // held before it is processed. Both come off the one number, so raising it moves them together.
    // Integer division truncates, so a rate that does not divide 1000 lands on the next rate up.
    private readonly int _minMouseIntervalMs = Math.Max(1, 1000 / profile.MaxMouseHz);

    // How far the physical cursor may drift from the warp point, as a fraction of the half-screen,
    // before it is parked back. It only has to stay clear of the local screen edges while the pointer
    // is on a virtual screen, and a warp is an X request plus a socket flush — paid at the mouse's
    // full polling rate if it happens once per sample.
    private const double WarpDeadZone = 0.10;

    private readonly IWorldState _peerState = peerState ?? new WorldState();
    private readonly Func<long> _getTickCount = getTickCount ?? (() => Environment.TickCount64);
    private readonly PeerPlatform _localPlatform = localPlatform ?? PeerPlatforms.Local;

    // channel-based actor model: single consumer processes all state mutations sequentially.
    // event tap callbacks post commands via TryWrite (non-blocking); async callers use TCS.
    private readonly LocalMasterState _state = new();
    private readonly Channel<Func<LocalMasterState, ValueTask>> _commands =
        Channel.CreateUnbounded<Func<LocalMasterState, ValueTask>>(
            new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    private Task? _consumerTask;
    private readonly Lock _mouseBatchLock = new();
    private CoalescingBatch<MouseInputKind, double>? _openMouseBatch;
    private int _postedMouseBatchCount;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    // how long stopping waits for the clipboard hash reads under way
    private static readonly TimeSpan HashReadsTimeout = TimeSpan.FromSeconds(2);
    private ITimer? _mouseFlushTimer;
    private long _lastMouseBatchPostTick;
    private bool _mouseFlushArmed;

    internal int PostedMouseBatchCount => Volatile.Read(ref _postedMouseBatchCount);

    private CancellationTokenSource? _pollCts;
    private readonly IScreenSaverSync _screenSaverSync = screenSaverSync;
    private readonly FileTransferService _fileTransfer = fileTransfer;
    private readonly IFileSelectionDetector _selectionDetector = selectionDetector;
    private readonly ClipboardMaster _clipboard = new(clipboardSync, log);
    // all three made first thing in StartAsync, before any handler that uses them is subscribed, since they call back into this router
    private ClipboardStreamSender _streams = null!;
    private ClipboardStreamReceiver _receiver = null!;
    private DeferredClipboardOffer _offer = null!;
    // the remote host the cursor is on, null on the local screen; read off the actor by the stream sender
    private volatile string? _cursorHost;
    // per host, the latest visit or hash request; a hash read off the actor is sent only while its token is still current
    private readonly Dictionary<string, int> _hashTokens = new(StringComparer.OrdinalIgnoreCase);
    // per host told Pending whose wait ended, the token of the hash read it is owed, which goes wherever the cursor is
    private readonly Dictionary<string, int> _owedHashes = new(StringComparer.OrdinalIgnoreCase);
    // guards _hashTokens, _owedHashes, _hashReads, _visits and _visitIds, and every write of _cursorHost; held to check a
    // hash and send it and to move the cursor's host, so no outdated hash follows an enter or leave; never held across a
    // clipboard read
    private readonly Lock _hashLock = new();
    private readonly List<Task> _hashReads = [];
    // per host, its latest visit, which its hashes carry and its pull requests echo; guarded by _hashLock
    private readonly Dictionary<string, int> _visits = new(StringComparer.OrdinalIgnoreCase);
    private int _visitIds;
    // cancelled when stopping begins: abandons a selection query and refuses new ones
    private readonly CancellationTokenSource _stopping = new();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _streams = new ClipboardStreamSender(relay, () => _cursorHost, log);
        _receiver = new ClipboardStreamReceiver(clipboardSync, (host, _) => _clipboard.Begun(host), _clipboard.Rejected, _time, log,
            MessageKind.ClipboardPullResponse);
        _receiver.Abandoned += (host, id) => OnStreamGivenUp(host, null, id, abort: true, failed: false);
        // on the timer thread too, so a newer stream from host may already be streaming: only the failed one's pull ends
        _receiver.Failed += failed => OnStreamGivenUp(failed.Host, failed.PullId, failed.Id, abort: !failed.SenderGaveUp, failed: true);
        _offer = new DeferredClipboardOffer(_time, log);
        _offer.TimedOut += host => _ = OfferDeferred(host);
        _offer.HeldWaitedOut += LandHeldInBackground;

        if (!platform.IsAccessibilityTrusted())
        {
            log.LogWarning("Accessibility permission not granted — open System Settings › Privacy & Security › Accessibility and enable Hydra, "
                + "then Hydra will continue automatically.");
            await platform.WaitForAccessibilityTrusted(cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;
            log.LogInformation("Accessibility permission granted");
        }

        if (log.IsEnabled(LogLevel.Information))
            log.LogInformation("Host: {Name}", profile.Name);

        if (!profile.RemoteOnly && profile.LocalHost == null && profile.Hosts.Count > 0)
        {
            log.LogError("Host '{Name}' is not listed in the config hosts — add it to the hosts list.", profile.Name);
            return;
        }

        var snapshot = await screens.Get(cancellationToken);

        // direct state init — safe because consumer has not started yet
        var st = _state;
        st.LocalScreens = snapshot.Screens;
        st.LocalScreenEntries = snapshot.Entries;
        st.ActiveLocalScreen = st.LocalScreens.FirstOrDefault();

        if (!profile.RemoteOnly && st.ActiveLocalScreen == null)
        {
            log.LogError("No local screens detected.");
            return;
        }

        if (st.ActiveLocalScreen != null)
            UpdateWarpPoint(st, st.ActiveLocalScreen);
        if (profile.RemoteOnly)
            st.LockedToScreen = true;  // default: locked to remote; hotkey unlocks to local
        st.Screens = BuildAllScreens(st.LocalScreens);
        st.Layout = new ScreenLayout(st.Screens, profile.Hosts, profile.DeadCorners, BuildScaleMap(st.LocalScreenEntries, []), log);

        if (log.IsEnabled(LogLevel.Information))
        {
            foreach (var remote in st.Screens.Where(r => !r.IsLocal))
                log.LogInformation("Remote screen '{Name}': waiting for peer", remote.Name);
        }

        relay.PeersChanged += OnPeersChanged;
        relay.MessageReceived += OnMessageReceived;
        relay.Disconnected += OnRelayDisconnected;
        screens.ScreensChanged += OnScreensChanged;

        _pollCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // one-shot, and armed only while a batch is waiting, so an idle master schedules nothing
        _mouseFlushTimer = _time.CreateTimer(_ => FlushMouseBatch(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        // start consumer before event tap so early events are processed
        _consumerTask = Task.Run(ProcessCommands, cancellationToken);

        await platform.StartEventTap((x, y) => OnMouseMove(x, y), OnMouseDelta, OnKeyEvent, OnMouseButton, OnMouseScroll,
            onLocalActivity: () => _ = _commands.Writer.TryWrite(_ => activityTracker.LocalActivity()));

        _screenSaverSync.ScreensaverActivated += OnScreensaverActivated;
        _screenSaverSync.ScreensaverDeactivated += OnScreensaverDeactivated;
        _screenSaverSync.ScreenLocked += OnLockDetected;
        _screenSaverSync.ScreenUnlocked += OnScreenUnlocked;
        if (profile.HideCursor)
            cursorHider.Hide();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        relay.PeersChanged -= OnPeersChanged;
        relay.MessageReceived -= OnMessageReceived;
        relay.Disconnected -= OnRelayDisconnected;
        screens.ScreensChanged -= OnScreensChanged;

        _screenSaverSync.ScreensaverActivated -= OnScreensaverActivated;
        _screenSaverSync.ScreensaverDeactivated -= OnScreensaverDeactivated;
        _screenSaverSync.ScreenLocked -= OnLockDetected;
        _screenSaverSync.ScreenUnlocked -= OnScreenUnlocked;
        platform.StopEventTap();

        // no more samples can arrive, so nothing may post a batch after the writer completes
        _mouseFlushTimer?.Dispose();
        _mouseFlushTimer = null;

        // drain remaining commands, then stop consumer
        _commands.Writer.TryComplete();
        if (_consumerTask != null)
            await _consumerTask;
        await AwaitHashReads(cancellationToken);

        // consumer is done; safe to access _state directly
        if (_state.Mouse.IsOnVirtualScreen)
        {
            platform.IsOnVirtualScreen = false;
            cursorHider.Show();
        }

        _receiver.Dispose();
        _offer.Dispose();
        await _streams.DisposeAsync();
    }

    private async Task ProcessCommands()
    {
        await foreach (var cmd in _commands.Reader.ReadAllAsync())
        {
            // guard EACH command: a throw from one command must not kill the consumer, which would
            // silently wedge all edge-crossing / relay-message routing until process restart.
            try
            {
                await cmd(_state);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "InputRouter command failed — continuing");
            }
        }
    }

    // posts a fence command and awaits it. the tcs is ALWAYS completed — even if the command throws
    // (logged) or the channel is closed — so a fence awaiter (screen/peer/disconnect handlers) can never
    // hang, the counterpart to ProcessCommands' per-command guard.
    private Task RunFence(Func<LocalMasterState, ValueTask> body)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_commands.Writer.TryWrite(async st =>
            {
                try { await body(st); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "InputRouter fence command failed"); }
                finally { tcs.TrySetResult(); }
            }))
            tcs.TrySetResult();
        return tcs.Task;
    }

    private Task<T> RunFence<T>(Func<LocalMasterState, T> body, T onFailure)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_commands.Writer.TryWrite(st =>
            {
                try { tcs.TrySetResult(body(st)); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "InputRouter fence command failed"); }
                finally { tcs.TrySetResult(onFailure); } // no-op if body already set a value; guarantees completion on any throw (incl. OCE)
                return ValueTask.CompletedTask;
            }))
            tcs.TrySetResult(onFailure);
        return tcs.Task;
    }

    // posts a fence command and awaits it — all previously queued commands will have been processed on return.
    // used by tests to synchronize after firing platform events. Seals first, so input captured into a
    // batch still waiting out its interval is part of what the fence covers.
    internal Task Flush()
    {
        if (_consumerTask == null) return Task.CompletedTask;
        SealMouseBatch();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_commands.Writer.TryWrite(_ => { tcs.TrySetResult(); return ValueTask.CompletedTask; }))
            return Task.CompletedTask;
        return tcs.Task;
    }

    internal Task<RouterStatus?> GetManagementStatus() => RunFence(st =>
    {
        var current = st.Mouse.CurrentScreen;
        return new RouterStatus(
            current != null,
            current?.Host,
            current?.Name,
            st.LockedToScreen,
            st.ConfinedToScreen,
            current != null && st.RelativeMouseScreens.GetValueOrDefault(current.Name));
    }, null);

    private async Task OnScreensChanged(LocalScreenSnapshot snapshot)
    {
        log.LogInformation("Screen configuration changed — rebuilding layout");
        LogDetectedScreens(snapshot.Screens);
        var newScreens = BuildAllScreens(snapshot.Screens);
        var peerScreens = await _peerState.GetPeerScreensSnapshot();

        await RunFence(async st =>
        {
            ApplyPeerScreenSizes(peerScreens, newScreens);
            st.LocalScreens = snapshot.Screens;
            st.LocalScreenEntries = snapshot.Entries;
            st.Screens = newScreens;
            st.Layout = new ScreenLayout(newScreens, profile.Hosts, profile.DeadCorners, BuildScaleMap(st.LocalScreenEntries, peerScreens), log);

            if (!st.Mouse.IsOnVirtualScreen)
            {
                st.ActiveLocalScreen = st.LocalScreens.FirstOrDefault() ?? st.ActiveLocalScreen;
                if (st.ActiveLocalScreen != null) UpdateWarpPoint(st, st.ActiveLocalScreen);
            }
            else if (st.LocalScreens.Count > 0 && (st.ActiveLocalScreen == null || !st.LocalScreens.Any(s => s.Contains(st.WarpX, st.WarpY))))
            {
                // a visit whose park point is on no screen (display off at boot, or its screen unplugged) parks on the
                // first one; a park point still on a screen is kept, so a parked cursor isn't dragged mid-visit
                st.ActiveLocalScreen = st.LocalScreens[0];
                UpdateWarpPoint(st, st.ActiveLocalScreen);
                await ParkOnEntry(st);
            }
        });
    }

    private async Task OnPeersChanged(string[] hostNames)
    {
        var current = new HashSet<string>(hostNames, StringComparer.OrdinalIgnoreCase);
        var configuredSlaves = profile.RemoteHosts
            .Select(s => s.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var delta = await _peerState.UpdatePeers(current, configuredSlaves);

        var disconnectedHost = await RunFence(st =>
        {
            string? host = null;
            if (st.Mouse.IsOnVirtualScreen && !current.Contains(st.Mouse.CurrentScreen.Host))
                host = ReturnHome(st, LocalLandingPoint(st), slaveReachable: false);

            if (delta.AnyDeparted) RebuildLayout(st, delta.PeerScreensSnapshot);
            if (st.RemoteCopyHost != null && !current.Contains(st.RemoteCopyHost)) AbandonRemoteCopy(st, "it left the relay");
            return host;
        }, null);

        if (disconnectedHost != null)
        {
            if (log.IsEnabled(LogLevel.Information))
                log.LogInformation("Remote peer '{Name}' disconnected — returned to local screen", disconnectedHost);
        }

        // abort a transfer only if its own peer left, not the cursor's
        _fileTransfer.AbortIfPeerGone(current, relay);
        _streams.Retain(current);
        var dropped = _receiver.Retain(current);
        // a host that left answers no pull, and one back under the same name must not have its answer taken
        var landed = new List<HeldLanding>();
        foreach (var pull in _clipboard.Outstanding.Where(p => !current.Contains(p.Host)))
            landed.AddRange(_clipboard.ForgetPull(pull.Host));
        await OfferDeferred(_offer.Retain(current));
        await TellDropped(dropped, "it left the relay");
        await Landed(landed);

        // send MasterConfig only to newly appeared peers that are configured as slaves
        foreach (var host in delta.NewPeers)
        {
            relay.Send(host, MessageKind.MasterConfig, new MasterConfigMessage(profile.LogLevel));
            if (log.IsEnabled(LogLevel.Debug))
                log.LogDebug("Sent MasterConfig to {Host}", host);
        }

        if (profile.RemoteOnly) await RunFence(async st => await TryEnterRemoteOnly(st));
    }

    private async Task OnRelayDisconnected()
    {
        var disconnectedHost = await RunFence(st =>
        {
            var host = ReturnHome(st, LocalLandingPoint(st), slaveReachable: false);
            if (st.RemoteCopyHost != null) AbandonRemoteCopy(st, "the relay disconnected");
            return host;
        }, null);

        // reset known peers so all slaves get a fresh MasterConfig on reconnect
        await _peerState.ClearPeers();
        _streams.DropAll();
        var dropped = _receiver.DropAll();
        var landed = _clipboard.ForgetPull();
        _offer.Clear();

        if (disconnectedHost != null)
        {
            _fileTransfer.Abort(relay, "relay disconnected");
            log.LogWarning("Relay disconnected — returned to local screen from '{Host}'", disconnectedHost);
        }
        await TellDropped(dropped, "the relay disconnected");
        await Landed(landed);
    }

    // an answer a host was waiting for that will never land: an honest empty, told where the cursor is
    private async ValueTask TellDropped(IReadOnlyList<DroppedClipboardStream> dropped, string why)
    {
        if (ClipboardStreamReceiver.LogDropped(dropped, why, log)) await ShowFailedWhereTheCursorIs();
    }

    // on the actor, which knows where the cursor is; once stopping, nothing is shown
    private Task ShowFailedWhereTheCursorIs() => RunFence(st =>
    {
        ShowOsd(st, ClipboardStreamReceiver.FailedOsd);
        return ValueTask.CompletedTask;
    });

    // a stream the receiver gave up on: its pull ends (failed: its answer not taken, so the next pull asks a resend), its
    // sender stops if abort (told it failed if failed), and its deferred offer goes
    private void OnStreamGivenUp(string host, int? pullId, long id, bool abort, bool failed)
    {
        _clipboard.StreamEnded(host, pullId, failed);
        if (abort) relay.SendClipboardAbort(host, id, failed);
        if (failed) _ = ShowFailedWhereTheCursorIs();
        _ = OfferDeferred(_offer.Abandoned(host, streamOnly: true, pullId));
    }

    private void OnScreensaverActivated()
    {
        _ = _commands.Writer.TryWrite(async st =>
        {
            if (st.ScreensaverActive) return;
            st.ScreensaverActive = true;

            if (st.Mouse.IsOnVirtualScreen)
            {
                st.SavedScreenName = st.Mouse.CurrentScreen.Name;
                st.SavedCursorX = (int)st.Mouse.X;
                st.SavedCursorY = (int)st.Mouse.Y;
                if (ReturnHome(st, LocalLandingPoint(st), slaveReachable: true) is { } leftHost)
                {
                    _fileTransfer.Abort(relay, "screensaver activated");
                    if (log.IsEnabled(LogLevel.Information))
                        log.LogInformation("Screensaver activated — returned to local screen from '{Host}'", leftHost);
                }
            }

            await BroadcastToPeers(MessageKind.ScreensaverSync, new ScreensaverSyncMessage(true));
            log.LogInformation("Screensaver activated — synced to slaves");
        });
    }

    private void OnScreensaverDeactivated()
    {
        _ = _commands.Writer.TryWrite(async st =>
        {
            if (!st.ScreensaverActive) return;
            st.ScreensaverActive = false;

            await BroadcastToPeers(MessageKind.ScreensaverSync, new ScreensaverSyncMessage(false));
            log.LogInformation("Screensaver deactivated — synced to slaves");

            // a remote-only master whose visit cannot be restored goes back to a remote now, keyboard-only use included
            if (!await TryRestoreAfterScreensaver(st)) await TryEnterRemoteOnly(st);
        });
    }

    // Re-enters the visit the screensaver interrupted, once it has ended. Refused for a held button, it stays pending
    // and is retried on local button ups and moves, ahead of a remote-only entry; any other outcome, or any entry from
    // the local screen, ends it.
    private async ValueTask<bool> TryRestoreAfterScreensaver(LocalMasterState st)
    {
        if (st.SavedScreenName is not { } saved || st.ScreensaverActive || st.Mouse.IsOnVirtualScreen) return false;
        var dest = relay.IsConnected ? st.Screens.FirstOrDefault(sc => !sc.IsLocal && sc.Name.EqualsIgnoreCase(saved)) : null;
        if (dest is not { Width: > 0 })
        {
            st.SavedScreenName = null;
            return false;
        }
        if (!await EnterFromLocal(st, dest, st.SavedCursorX, st.SavedCursorY, st.ExitEdge)) return false;
        if (log.IsEnabled(LogLevel.Information))
            log.LogInformation("Restored cursor to '{Screen}' after screensaver", saved);
        return true;
    }

    private async ValueTask BroadcastToPeers<T>(MessageKind kind, T message)
    {
        var peerScreens = await _peerState.GetPeerScreensSnapshot();
        var hosts = peerScreens.Keys.ToArray();
        if (hosts.Length == 0) return;
        relay.Send(hosts, kind, message);
    }

    private void OnLockDetected()
    {
        if (!profile.ScreenLockPropagation) return;
        _ = _commands.Writer.TryWrite(async st =>
        {
            log.LogInformation("Machine locked — propagating to slaves");
            await BroadcastLockScreen(st);
        });
    }

    private void OnScreenUnlocked()
    {
        _ = _commands.Writer.TryWrite(async _ =>
        {
            log.LogInformation("Screen unlocked — restarting event tap");
            await platform.RestartEventTap();
        });
    }

    private ValueTask BroadcastLockScreen(LocalMasterState st) =>
        BroadcastToPeers(MessageKind.LockScreen, new LockScreenMessage(_getTickCount() - st.LastInputTick));

    // sends master's clipboard hash to slave; slave decides whether to request the full push. The read runs off the
    // actor, after EnterScreen went out; its hash goes only if no newer entry or request for host came since and the
    // cursor is still there, or, owed: host was told Pending and its wait ended, so it goes wherever the cursor is. A
    // read replacing an owed one still under way is owed in its place
    private void PushClipboardToHost(string host, bool owed = false)
    {
        if (!UseHydraClipboard(host)) return;
        lock (_hashLock)
        {
            if (_stopping.IsCancellationRequested) return;
            owed |= _owedHashes.ContainsKey(host);
            if (owed) _owedHashes[host] = NextHashToken(host);
            var token = owed ? _owedHashes[host] : NextHashToken(host);
            TrackBackground(() => Task.Run(() => SendHashIfCurrent(host, token)));
        }
    }

    // starts work off the caller's thread, among the reads StopAsync awaits, unless stopping, when none may start after it
    private void TrackBackground(Func<Task> work)
    {
        lock (_hashLock)
        {
            if (_stopping.IsCancellationRequested) return;
            _hashReads.RemoveAll(t => t.IsCompleted);
            _hashReads.Add(work());
        }
    }

    // the hosts told Pending whose wait ended, sent their hash wherever the cursor is, stamped with their latest visit;
    // which they were
    private IReadOnlyList<string> SendOwedHashes()
    {
        var due = _offer.TakeDue();
        foreach (var host in due)
        {
            if (log.IsEnabled(LogLevel.Debug))
                log.LogDebug("Clipboard hash to {Host}: told pending, its wait ended", host);
            PushClipboardToHost(host, owed: true);
        }
        return due;
    }

    // for tests: runs as an owed read that found nothing is about to be owed on the clipboard still on its way
    internal Action? OwingOnArriving { get; set; }

    // completes once every hash read started so far has sent or dropped its hash
    internal Task HashReads()
    {
        lock (_hashLock)
            return Task.WhenAll([.. _hashReads]);
    }

    // after the actor drained: the hash reads under way, bounded, so none sends after the router stopped
    private async ValueTask AwaitHashReads(CancellationToken cancellationToken)
    {
        var reads = HashReads();
        if (reads.IsCompleted) return;
        var waited = reads.WaitAsync(HashReadsTimeout, _time, cancellationToken);
        if (log.IsEnabled(LogLevel.Debug))
            log.LogDebug("Waiting for the clipboard hash reads under way");
        try
        {
            await waited;
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            log.LogWarning("Clipboard hash reads still under way after {Timeout}; stopping without them", HashReadsTimeout);
        }
    }

    // a new visit to host, or a new hash request for it, outdating every read still under way for it; under _hashLock
    private int NextHashToken(string host) => _hashTokens[host] = _hashTokens.GetValueOrDefault(host) + 1;

    private void SendHashIfCurrent(string host, int token)
    {
        try
        {
            // a host whose copy the master never took (or has yet to) keeps it rather than empty it for an older push
            var read = _clipboard.Hash(host);
            IReadOnlyList<OutstandingPull> landNow = [];
            List<OutstandingPull> gone = [];
            lock (_hashLock)
            {
                // a host owed its hash gets it wherever the cursor is
                var owed = _owedHashes.TryGetValue(host, out var owedToken) && owedToken == token;
                if (owed) _owedHashes.Remove(host);
                var current = !_stopping.IsCancellationRequested && _hashTokens.TryGetValue(host, out var now) && now == token;
                if (read is not { } hash)
                {
                    if (!owed || !current) return;
                    // the master emptied for a clipboard still on its way (streaming, or held for a newer pull, within its
                    // deadline): owed again once that lands, rather than told none follows
                    if (_clipboard.StreamingIn is var streams && _clipboard.HeldFor is var held && streams.Count + held.Count > 0)
                    {
                        OwingOnArriving?.Invoke();
                        if (log.IsEnabled(LogLevel.Debug))
                            log.LogDebug("Clipboard hash to {Host}: nothing yet, owed once the clipboard on its way lands", host);
                        landNow = _offer.OweOn(host, streams, held);
                        // a stream that ended between the snapshot and the owing found no wait to end: it ends here
                        foreach (var ended in streams.Where(p => !_clipboard.IsStreaming(p)))
                            gone.Add(ended);
                    }
                    // nothing to push: a host owed its hash may await a push that was aborted, so it is told none follows
                    else SendNoPush(host, _visits.GetValueOrDefault(host));
                }
                else if (!current || (!owed && !host.EqualsIgnoreCase(_cursorHost)))
                {
                    if (log.IsEnabled(LogLevel.Debug))
                        log.LogDebug("Clipboard hash to {Host} dropped: outdated", host);
                }
                else
                {
                    if (log.IsEnabled(LogLevel.Debug))
                        log.LogDebug("Sending clipboard hash to {Host}", host);
                    relay.Send(host, MessageKind.ClipboardHash, hash with { Visit = _visits.GetValueOrDefault(host) });
                }
            }
            // held for a pull waited out already: it lands now, which offers it to the hosts owed on it
            foreach (var pull in landNow) LandHeldInBackground(pull);
            foreach (var ended in gone) _ = OfferDeferred(_offer.Abandoned(ended.Host, streamOnly: true, ended.Id));
        }
        catch (Exception ex)
        {
            // an owed read that failed is no longer under way, so the host's next ordinary read is not owed
            lock (_hashLock)
                if (_owedHashes.TryGetValue(host, out var owedToken) && owedToken == token) _owedHashes.Remove(host);
            log.LogWarning(ex, "Failed to send clipboard hash to {Host}", host);
        }
    }

    // the clipboard hash on entering host, on the actor; while pulls to other hosts are outstanding it waits for their
    // answers, which may change what the master offers, unless those pulls have already been waited out. owed: host is
    // owed its hash (an owed read was under way), which its entry's read or wait stays
    private void OfferClipboardOnEntry(string host, bool owed)
    {
        if (!UseHydraClipboard(host)) return;
        var outstanding = _clipboard.Outstanding;
        if (outstanding.Count > 0)
        {
            var offer = _offer.Defer(host, outstanding, _clipboard.IsStreaming, owed);
            // answered meanwhile, before the wait was there to be ended: it ends here
            if (offer != DeferredOffer.SendNow && _offer.Resolve(_clipboard.Outstanding) == null)
            {
                if (offer == DeferredOffer.WaitPending) SendPending(host);
                SendOwedHashes();
                return;
            }
        }
        owed |= _offer.SendingNow(host, _clipboard.Outstanding);
        SendOwedHashes();
        PushClipboardToHost(host, owed);
    }

    // a deferred entry hash, once its wait has ended: to host, if the cursor is still there; any host told Pending whose
    // wait ended is sent its own wherever the cursor is
    private Task OfferDeferred(string? host)
    {
        var due = SendOwedHashes();
        if (host == null || due.Contains(host, StringComparer.OrdinalIgnoreCase)) return Task.CompletedTask;
        return RunFence(st =>
        {
            if (IsCursorOn(st, host)) PushClipboardToHost(host);
            return ValueTask.CompletedTask;
        });
    }

    // the pull's answer is streaming in, emptying the master's clipboard: host, waiting on it, empties its own meanwhile
    private async ValueTask OfferPending(string? host)
    {
        if (host == null) return;
        await RunFence(st =>
        {
            if (IsCursorOn(st, host)) SendPending(host);
            return ValueTask.CompletedTask;
        });
    }

    private void SendPending(string host)
    {
        if (log.IsEnabled(LogLevel.Debug))
            log.LogDebug("Sending clipboard pending to {Host}", host);
        // a host whose copy the master never took keeps it rather than empty it for the clipboard on its way
        relay.Send(host, MessageKind.ClipboardHash, new ClipboardHashMessage(null, Pending: true, ResendFrom: _clipboard.ResendFrom(host)));
    }

    private static bool IsCursorOn(LocalMasterState st, string host) => st.Mouse.IsOnVirtualScreen && st.Mouse.CurrentScreen.Host.EqualsIgnoreCase(host);

    // slave has compared hashes and determined it needs our clipboard; send the full push, or tell it none follows so it
    // is not left waiting. On the relay receive path, never the actor
    private void OnClipboardPullRequest(string host, int? visit)
    {
        if (!UseHydraClipboard(host)) return;
        try
        {
            if (_clipboard.Push() is { } push)
            {
                // one that cannot be encoded never goes, so the slave is told none follows rather than wait on it
                _streams.Send(host, MessageKind.ClipboardPush, push, () => SendNoPush(host, visit));
                return;
            }
            if (log.IsEnabled(LogLevel.Debug))
                log.LogDebug("Clipboard pull request from {Host}: nothing to push", host);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to push clipboard to {Host}", host);
        }
        SendNoPush(host, visit);
    }

    private void SendNoPush(string host, int? visit) => relay.Send(host, MessageKind.ClipboardPushNone, new ClipboardPushNoneMessage(visit));

    // whether a pull request from host for visit is its latest visit's, which the cursor may have left since: that
    // visit's hash emptied the slave's clipboard for the push, so it is still honoured
    private bool IsLatestVisit(string host, int? visit)
    {
        lock (_hashLock)
            return _visits.TryGetValue(host, out var latest) && latest == visit;
    }

    private void PullClipboardFromHost(string host)
    {
        if (!UseHydraClipboard(host)) return;
        if (log.IsEnabled(LogLevel.Debug))
            log.LogDebug("Pulling clipboard from {Host}", host);
        // a hash read under way for host is older than this pull, and would reach it after the pull's answer, emptying
        // the copy it answered with: outdated; one owed is read afresh once the pull is out, unless the deferred offer
        // owes it too, whose wait's end sends it
        bool owed;
        lock (_hashLock)
        {
            owed = _owedHashes.Remove(host);
            NextHashToken(host);
        }
        relay.Send(host, MessageKind.ClipboardPull, _clipboard.Pull(host, _receiver.PendingFrom));
        if (owed && !_offer.Owes(host)) PushClipboardToHost(host, owed: true);
    }

    // takes a slave's answer to a pull, whole or streamed, offering the cursor's remote the result; whether it was taken
    private async ValueTask<bool> OnClipboardPullAnswer(string host, ClipboardPullResponseMessage answer)
    {
        var taken = _clipboard.TakePullAnswer(host, answer);
        if (!taken.Taken) return false;
        await Offer(new OutstandingPull(host, answer.PullId), taken);
        return true;
    }

    // what an answer to pull did: the pushes of an older clipboard stop, each host whose push stopped, bar the cursor's,
    // owed the new clipboard's hash wherever the cursor is; superseded streams go, and the cursor's remote is offered the result
    private async ValueTask Offer(OutstandingPull pull, PullAnswer taken)
    {
        // held for a newer pull to its host, it lands with that pull's answer, or once that pull is waited out: what
        // waited on it waits on that pull; one waited out already lands it now
        if (taken.HeldFor is { } heldFor)
        {
            if (_offer.Held(heldFor)) await LandHeld(heldFor);
            return;
        }
        if (taken.Failed) await ShowFailedWhereTheCursorIs();
        IReadOnlyList<string> aborted = taken.Applied is { HasClipboard: true } ? _streams.AbortPushes() : [];
        AbortSuperseded(taken.Superseded);
        var waiting = _offer.Answered(pull, taken.Superseded);
        var due = SendOwedHashes();
        if (taken.Applied == null && waiting == null) return;
        var activeHost = await RunFence(st => st.Mouse.CurrentScreen?.Host, null);
        foreach (var host in aborted.Where(h => !h.EqualsIgnoreCase(activeHost) && !due.Contains(h, StringComparer.OrdinalIgnoreCase)))
        {
            if (log.IsEnabled(LogLevel.Debug))
                log.LogDebug("Clipboard hash to {Host}: its push of the older clipboard was aborted", host);
            PushClipboardToHost(host, owed: true);
        }
        // a host still waiting on another pull is offered the result once that one resolves too; the cursor's host, its
        // push aborted, is owed it then, or now, wherever the cursor is
        if (activeHost == null) return;
        var activeAborted = aborted.Contains(activeHost, StringComparer.OrdinalIgnoreCase);
        if (activeAborted) _offer.Owe(activeHost);
        if (!_offer.IsWaiting(activeHost) && !due.Contains(activeHost, StringComparer.OrdinalIgnoreCase))
            PushClipboardToHost(activeHost, owed: activeAborted);
    }

    // the newer pull an answer is held for was waited out: the held answer lands, offered as any answer, and what waited
    // on that pull waits no more (the waits on the stream moved there); never once stopping
    private async Task LandHeld(OutstandingPull pull)
    {
        if (_stopping.IsCancellationRequested) return;
        if (_clipboard.LandHeld(pull) is { } landed) await Landed([landed]);
        await OfferDeferred(_offer.Answered(pull, []));
    }

    // LandHeld off the caller's thread (a timer's, a hash read's), tracked as hash reads are, so stopping awaits it
    private void LandHeldInBackground(OutstandingPull pull) => TrackBackground(() => Task.Run(() => LandHeld(pull)));

    // a held answer landed as its host's pulls were forgotten, offered as any answer; one that could not be written is a
    // failure, shown where the cursor is
    private async ValueTask Landed(IReadOnlyList<HeldLanding> landed)
    {
        foreach (var held in landed)
        {
            if (held.Answer is { } answer) await Offer(held.Pull, answer);
            else await ShowFailedWhereTheCursorIs();
        }
    }

    // the answer streaming in for pull is a changed clipboard, so it supersedes the older pulls from its Begin: their
    // streams are dropped and aborted, and a host whose entry hash waited only on them is offered the clipboard; replacing:
    // the Begin replaced host's older stream, whose pull its accept forgot; resends: the Begin's Resends, which ranks it
    private async ValueTask SupersedeFromBegin(OutstandingPull pull, bool replacing, int? resends)
    {
        var superseded = _clipboard.Streaming(pull, resends);
        if (superseded.Count == 0 && !replacing) return;
        // host's own older answer is the stream this one replaced, so nothing of it is left to abort
        AbortSuperseded([.. superseded.Where(p => !p.Host.EqualsIgnoreCase(pull.Host))]);
        await OfferDeferred(_offer.Resolve(_clipboard.Outstanding));
    }

    // the answers to superseded pulls are no longer wanted: their streams still arriving are dropped and aborted
    private void AbortSuperseded(IReadOnlyList<OutstandingPull> superseded)
    {
        foreach (var older in superseded)
        {
            if (_receiver.Drop(older.Host) is { } dropped) relay.SendClipboardAbort(older.Host, dropped.Id);
        }
    }

    private async Task OnMessageReceived(string sourceHost, MessageKind kind, ReadOnlyMemory<byte> body)
    {
        switch (kind)
        {
            case MessageKind.ScreenInfo:
                var info = body.ParseMessage<ScreenInfoMessage>(log, sourceHost);
                var owned = ScreenNaming.OwnedBy(sourceHost, info?.Screens ?? []);
                if (owned.Refused.Count > 0 && log.IsEnabled(LogLevel.Warning))
                    log.LogWarning("{Host} sent {Screens} {Names}, which are not its own or repeat one it already sent; ignored",
                        sourceHost, Pluralize.Of(owned.Refused.Count, "screen"), string.Join(", ", owned.Refused));
                if (info != null && owned.Kept.Count > 0)
                {
                    await _peerState.SetPeerScreens(sourceHost, owned.Kept);

                    _peerState.SetPeerPlatform(sourceHost, info.Platform);
                    if (!UseHydraClipboard(sourceHost) && log.IsEnabled(LogLevel.Information))
                        log.LogInformation("Clipboard sync with {Host}: using macOS Universal Clipboard; Hydra is standing down", sourceHost);
                    var snapshot = await _peerState.GetPeerScreensSnapshot();
                    await RunFence(async st =>
                    {
                        RebuildLayout(st, snapshot);
                        await TryEnterRemoteOnly(st);
                    });
                    if (log.IsEnabled(LogLevel.Information))
                        log.LogInformation("Screen info from {Host}: {Count} {Screens}",
                            sourceHost, owned.Kept.Count, Pluralize.Of(owned.Kept.Count, "screen"));
                }
                break;
            case MessageKind.SlaveLog:
                var entry = body.ParseMessage<SlaveLogMessage>(log, sourceHost);
                if (entry != null) ForwardSlaveLog(sourceHost, entry);
                break;
            case MessageKind.ScreensaverSync:
                break; // master never acts on screensaver sync messages
            case MessageKind.LatencyProbe or MessageKind.LatencyProbeResponse:
            case MessageKind.RemoteManagementRequest or MessageKind.RemoteManagementResponse:
                break; // RelayLatencyService and RemoteManagementService subscribe to these themselves
            case MessageKind.ActivityPing:
                _ = _commands.Writer.TryWrite(_ => activityTracker.RemoteActivity(sourceHost));
                break;
            case MessageKind.ClipboardPullRequest:
                {
                    if (!UseHydraClipboard(sourceHost)) break;
                    var visit = body.ParseMessage<ClipboardPullRequestMessage>(log, sourceHost)?.Visit;
                    var onThatScreen = await RunFence(st => IsCursorOn(st, sourceHost), false);
                    if (onThatScreen || IsLatestVisit(sourceHost, visit))
                        OnClipboardPullRequest(sourceHost, visit);
                    else
                    {
                        if (log.IsEnabled(LogLevel.Debug))
                            log.LogDebug("Clipboard pull request from {Host} ignored (cursor not on that screen, and not its latest visit)", sourceHost);
                        SendNoPush(sourceHost, visit);
                    }
                    break;
                }
            case MessageKind.ClipboardPullResponse:
                {
                    if (!UseHydraClipboard(sourceHost)) break;
                    var clip = body.ParseMessage<ClipboardPullResponseMessage>(log, sourceHost);
                    if (clip == null) break;
                    // a changed whole answer is newer than any stream still pending from that host; an unchanged one
                    // answers a newer pull beside it, the stream still landing as the answer to its own
                    var changed = clip.Unchanged != true;
                    if (changed) _receiver.Drop(sourceHost);
                    await OnClipboardPullAnswer(sourceHost, clip);
                    if (changed) _clipboard.StreamEnded(sourceHost);
                    break;
                }
            case MessageKind.ClipboardStreamBegin:
                {
                    if (!UseHydraClipboard(sourceHost)) break;
                    if (body.ParseMessage<ClipboardStreamBeginMessage>(log, sourceHost) is not { } begin) break;
                    // accepting the Begin notes its pull streaming in before the receiver takes it, so a pull made meanwhile
                    // keeps it outstanding; it carries the stream's hash once the receiver has taken the Begin. Every end
                    // below and in the receiver's events ends it
                    var answering = begin is { Inner: MessageKind.ClipboardPullResponse, PullId: { } pullId } ? _clipboard.StreamFor(sourceHost, pullId) : null;
                    // the Begin answers the pull an older answer was held for, which its accept dropped: so goes its deadline
                    if (answering != null) _offer.HeldReplaced(answering);
                    // a stream still pending from that host is replaced by this Begin, its pull no longer outstanding
                    var replacing = answering != null && _receiver.PendingFrom(sourceHost) != null;
                    // an answer to no outstanding pull (superseded by a newer copy, or forgotten) would only empty the clipboard
                    if (begin.Inner == MessageKind.ClipboardPullResponse && answering == null)
                    {
                        if (log.IsEnabled(LogLevel.Debug))
                            log.LogDebug("Clipboard stream {Id} from {Host} answers no outstanding pull; aborted", begin.Id, sourceHost);
                        relay.SendClipboardAbort(sourceHost, begin.Id);
                        break;
                    }
                    // a copy on the master since the pull is newer: the stream is refused before it empties that copy, and
                    // replaces any older one from that host as a Begin taken would, whose pull its accept forgot
                    if (answering != null && _clipboard.RefuseStream(answering))
                    {
                        if (replacing) _receiver.Drop(sourceHost);
                        relay.SendClipboardAbort(sourceHost, begin.Id);
                        await OfferDeferred(_offer.Answered(answering, []));
                        if (replacing) await OfferDeferred(_offer.Resolve(_clipboard.Outstanding));
                        break;
                    }
                    // a rejected Begin is no answer begun, so the wait runs on; one the receiver did not take answers nothing
                    if (!_receiver.OnBegin(sourceHost, begin))
                    {
                        if (answering != null) _clipboard.StreamEnded(sourceHost, answering.Id);
                        if (replacing) await OfferDeferred(_offer.Resolve(_clipboard.Outstanding));
                        break;
                    }
                    if (answering != null)
                    {
                        await OfferPending(_offer.Streaming(sourceHost, answering.Id));
                        await SupersedeFromBegin(answering, replacing, begin.Resends);
                    }
                    break;
                }
            case MessageKind.ClipboardStreamChunk:
                {
                    if (!UseHydraClipboard(sourceHost)) break;
                    if (body.ParseMessage<ClipboardStreamChunkMessage>(log, sourceHost) is not { } chunk) break;
                    // the receiver takes only pull answers, so a completed stream is one
                    if (_receiver.OnChunk(sourceHost, chunk) is not { } completed) break;
                    if (completed.Bytes.TryDecodeBody<ClipboardPullResponseMessage>() is not { } streamed)
                    {
                        _receiver.OnUnreadable(sourceHost, chunk.Id);
                        break;
                    }
                    if (!await OnClipboardPullAnswer(sourceHost, streamed))
                        await OfferDeferred(_offer.Abandoned(sourceHost, streamOnly: true));
                    break;
                }
            case MessageKind.ClipboardStreamAbort:
                {
                    if (!UseHydraClipboard(sourceHost)) break;
                    if (body.ParseMessage<ClipboardStreamAbortMessage>(log, sourceHost) is not { } abort) break;
                    // each side's ids are its own, so each ignores one it doesn't hold
                    var receiving = _receiver.PendingFrom(sourceHost) != null;
                    _receiver.OnAbort(sourceHost, abort);
                    _streams.OnAbort(sourceHost, abort);
                    // the stream a waiting host was told is pending won't land; a newer one is applied and offered as usual; one
                    // the sender gave up on ended as a failure
                    if (receiving && abort.Failed != true && _receiver.PendingFrom(sourceHost) == null)
                    {
                        _clipboard.StreamEnded(sourceHost);
                        // a newer pull to that host still out: its answer superseded the stream, so what waited on the stream waits on it
                        if (_clipboard.Outstanding.LastOrDefault(p => p.Host.EqualsIgnoreCase(sourceHost)) is { } newer) _offer.Superseded(newer);
                        else await OfferDeferred(_offer.Abandoned(sourceHost, streamOnly: true));
                    }
                    break;
                }
            case MessageKind.FileSelectionResponse:
                {
                    var response = body.ParseMessage<FileSelectionResponseMessage>(log, sourceHost);
                    // on the consumer, so no copy can be pressed between the check and the buffer write
                    var outcome = await RunFence(st => ApplyRemoteSelection(st, sourceHost, response), RemoteSelectionOutcome.NotApplied);
                    if (outcome == RemoteSelectionOutcome.Superseded && log.IsEnabled(LogLevel.Debug))
                        log.LogDebug("Copy hotkey: dropping superseded selection response from {Host}", sourceHost);
                    else if (outcome == RemoteSelectionOutcome.NotApplied && log.IsEnabled(LogLevel.Debug))
                        log.LogDebug("Copy hotkey: selection response from {Host} not applied: the router has stopped or the command failed", sourceHost);
                    break;
                }
            case MessageKind.FileTransferBusy:
                {
                    _fileTransfer.HandleBusy(sourceHost);
                    _ = _commands.Writer.TryWrite(st =>
                    {
                        if (IsAwaitedRemoteCopy(st, sourceHost)) AbandonRemoteCopy(st, "it is busy with a transfer");
                        ShowOsd(st, FileTransferService.InProgressOsd);
                        return ValueTask.CompletedTask;
                    });
                    break;
                }
            case var _ when FileTransferService.IsFileTransferMessage(kind):
                {
                    var wasSendingTo = _fileTransfer.IsSendingTo(sourceHost);
                    var wasCoordinating = _fileTransfer.IsCoordinatingTransferTo(sourceHost);
                    if (wasSendingTo || wasCoordinating)
                    {
                        if (kind == MessageKind.FileTransferAccepted)
                            SendOsd(sourceHost, FileTransferService.PastingOsd);
                        else if (kind == MessageKind.FileTransferAbort)
                        {
                            var abort = body.TryDecodeBody<FileTransferAbortMessage>();
                            if (abort?.Reason == FileTransferService.ReasonNoFolder)
                                SendOsd(sourceHost, FileTransferService.InvalidTargetOsd);
                        }
                    }
                    if (await _fileTransfer.OnMessage(sourceHost, kind, body, relay))
                        osd.Show(FileTransferService.PastedOsd);
                    break;
                }
            default:
                if (log.IsEnabled(LogLevel.Debug))
                    log.LogDebug("Unhandled message kind {Kind} from {Host}", kind, sourceHost);
                break;
        }
    }

    // the latest local selection query, done once its report is queued on the consumer or it was cancelled
    internal Task LocalSelectionQuery { get; private set; } = Task.CompletedTask;

    // queries off the consumer and reports back through it, so routing carries on meanwhile
    private void CopyLocalSelection(LocalMasterState st)
    {
        if (_stopping.IsCancellationRequested)
        {
            if (log.IsEnabled(LogLevel.Debug))
                log.LogDebug("Copy hotkey: stopping, so not asking {Name}", _selectionDetector.FileManagerName);
            return;
        }
        var generation = ++st.CopyGeneration;
        st.CopyPending = true;
        st.RemoteCopyHost = null;
        var stopping = _stopping.Token;
        LocalSelectionQuery = Task.Run(() =>
        {
            FileSelectionResult result;
            try
            {
                result = _selectionDetector.GetSelectedPaths(stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                log.LogDebug("Copy hotkey: selection query abandoned by stopping");
                return;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Copy hotkey: file selection query failed");
                result = FileSelectionResult.Failure;
            }
            _ = _commands.Writer.TryWrite(s =>
            {
                ReportLocalSelection(s, generation, result);
                return ValueTask.CompletedTask;
            });
        });
    }

    // a remote copy supersedes any local query still running, and only the latest press's host may answer
    private static void CopyRemoteSelection(LocalMasterState st, string host)
    {
        st.CopyGeneration++;
        st.CopyPending = true;
        st.RemoteCopyHost = host;
    }

    private static bool IsAwaitedRemoteCopy(LocalMasterState st, string host) => st.RemoteCopyHost?.EqualsIgnoreCase(host) == true;

    // each remote press takes its host's first answer, shown wherever the cursor is now
    private RemoteSelectionOutcome ApplyRemoteSelection(LocalMasterState st, string host, FileSelectionResponseMessage? response)
    {
        if (!IsAwaitedRemoteCopy(st, host)) return RemoteSelectionOutcome.Superseded;
        st.CopyPending = false;
        st.RemoteCopyHost = null;
        ShowOsd(st, response == null ? FileSelectionResult.FailedMessage : _fileTransfer.HandleSelectionResponse(host, response));
        return RemoteSelectionOutcome.Applied;
    }

    // the awaited host will not answer, so a paste need not wait for it
    private void AbandonRemoteCopy(LocalMasterState st, string reason)
    {
        if (log.IsEnabled(LogLevel.Information))
            log.LogInformation("Copy hotkey: no selection coming from {Host}: {Reason}", st.RemoteCopyHost, reason);
        st.CopyPending = false;
        st.RemoteCopyHost = null;
    }

    // only the latest press reports; an earlier query answering late is dropped
    private void ReportLocalSelection(LocalMasterState st, long generation, FileSelectionResult result)
    {
        if (generation != st.CopyGeneration)
        {
            log.LogDebug("Copy hotkey: dropping superseded selection result");
            return;
        }
        st.CopyPending = false;
        var name = _selectionDetector.FileManagerName;
        result.Log(log, "Copy hotkey", name);
        ShowOsd(st, _fileTransfer.HandleSelectionResponse(profile.Name, result.ToResponse(name)));
    }

    // routes OSD to slave when cursor is remote, otherwise shows locally on master
    private void ShowOsd(LocalMasterState st, string message) =>
        SendOsd(st.Mouse.IsOnVirtualScreen ? st.Mouse.CurrentScreen.Host : profile.Name, message);

    // sends OSD to a specific known host (file transfer outcomes, etc.)
    private void SendOsd(string targetHost, string message)
    {
        if (targetHost.EqualsIgnoreCase(profile.Name))
            osd.Show(message);
        else
            relay.Send(targetHost, MessageKind.Osd, new OsdMessage(message));
    }

    private void ForwardSlaveLog(string sourceHost, SlaveLogMessage entry)
    {
        var category = $"slave:{sourceHost}/{entry.Category}";
        var logger = _peerState.GetOrCreateSlaveLogger(category, loggerFactory);

        var level = (LogLevel)entry.Level;
        if (!logger.IsEnabled(level)) return;
        if (entry.Exception != null)
            logger.Log(level, "{Message}\n{Exception}", entry.Message, entry.Exception);
        else
            logger.Log(level, "{Message}", entry.Message);
    }

    // rebuilds screens/layout from localScreens/peerScreens; must be called from consumer
    private void RebuildLayout(LocalMasterState st, Dictionary<string, List<ScreenInfoEntry>> peerScreens)
    {
        if (!profile.RemoteOnly && st.ActiveLocalScreen == null) return;

        var newScreens = BuildAllScreens(st.LocalScreens);
        ApplyPeerScreenSizes(peerScreens, newScreens);
        var newLayout = new ScreenLayout(newScreens, profile.Hosts, profile.DeadCorners, BuildScaleMap(st.LocalScreenEntries, peerScreens), log);
        st.Screens = newScreens;
        st.Layout = newLayout;
        st.ActiveLocalScreen = st.ActiveLocalScreen == null ? null
            : st.LocalScreens.FirstOrDefault(s => s.Name.EqualsIgnoreCase(st.ActiveLocalScreen.Name))
                ?? st.LocalScreens.FirstOrDefault() ?? st.ActiveLocalScreen;

        // prune stale relative-mode entries for screens that no longer exist
        var validNames = new HashSet<string>(st.Screens.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var key in st.RelativeMouseScreens.Keys.Where(k => !validNames.Contains(k)).ToList())
            st.RelativeMouseScreens.Remove(key);

        // if the cursor is on a remote screen whose dims changed, update it
        if (st.Mouse.IsOnVirtualScreen)
        {
            var refreshed = st.Screens.FirstOrDefault(s => s.Name.EqualsIgnoreCase(st.Mouse.CurrentScreen.Name));
            if (refreshed != null && refreshed != st.Mouse.CurrentScreen)
            {
                var remoteInfo = GetRemoteScreensAndScales(st.Screens, peerScreens, refreshed);
                st.Mouse.EnterScreen(refreshed, remoteInfo.Screens, (int)st.Mouse.X, (int)st.Mouse.Y,
                    remoteInfo.ScaleMap.GetValueOrDefault(refreshed.Name, 1.0m), remoteInfo.ScaleMap, remoteInfo.RelativeScaleMap);
            }
        }
    }

    private static Dictionary<string, decimal> BuildScaleMap(
        List<ScreenInfoEntry> localEntries, Dictionary<string, List<ScreenInfoEntry>> peerScreens)
    {
        var map = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in localEntries)
            map[e.Name] = e.MouseScale;
        foreach (var entries in peerScreens.Values)
            foreach (var e in entries)
                map[e.Name] = e.MouseScale;
        return map;
    }

    // replaces per-host placeholders with actual per-screen entries from ScreenInfo; must be called from consumer
    private static void ApplyPeerScreenSizes(Dictionary<string, List<ScreenInfoEntry>> peerScreens, List<ScreenRect> screens)
    {
        for (var i = screens.Count - 1; i >= 0; i--)
        {
            var screen = screens[i];
            if (!screen.IsLocal && peerScreens.TryGetValue(screen.Host, out var entries))
            {
                screens.RemoveAt(i);
                // insert in reverse so final order matches ScreenInfo order
                for (var j = entries.Count - 1; j >= 0; j--)
                {
                    var e = entries[j];
                    screens.Insert(i, new ScreenRect(e.Name, screen.Host, e.X, e.Y, e.Width, e.Height, IsLocal: false));
                }
            }
        }
    }

    private static ScreenInfoEntry? FindRemoteEntry(Dictionary<string, List<ScreenInfoEntry>> peerScreens, ScreenRect screen)
    {
        if (peerScreens.TryGetValue(screen.Host, out var entries))
            return entries.FirstOrDefault(e => e.Name.EqualsIgnoreCase(screen.Name));
        return null;
    }

    // builds remoteScreens list + scaleMaps for a given destination host; used when entering a remote screen
    private static RemoteScreenInfo GetRemoteScreensAndScales(
        List<ScreenRect> allScreens, Dictionary<string, List<ScreenInfoEntry>> peerScreens, ScreenRect target)
    {
        var screens = allScreens.Where(s => !s.IsLocal && s.Host.EqualsIgnoreCase(target.Host)).ToList();
        var scaleMap = screens.ToDictionary(s => s.Name, s => FindRemoteEntry(peerScreens, s)?.MouseScale ?? 1.0m, StringComparer.OrdinalIgnoreCase);
        var relativeScaleMap = screens.ToDictionary(s => s.Name, s => FindRemoteEntry(peerScreens, s)?.RelativeMouseScale, StringComparer.OrdinalIgnoreCase);
        return new RemoteScreenInfo(screens, scaleMap, relativeScaleMap);
    }

    private void OnKeyEvent(KeyEvent keyEvent)
    {
        SealMouseBatch();
        _ = _commands.Writer.TryWrite(async st =>
        {
            st.LastInputTick = _getTickCount();
            await activityTracker.LocalActivity();
            if (st.Mouse.IsOnVirtualScreen && log.IsEnabled(LogLevel.Debug))
                log.LogDebug("Key: {Type}{Label} mods={Modifiers}", keyEvent.Type,
                    keyEvent.Character.HasValue ? $" '{keyEvent.Character}'" : keyEvent.Key.HasValue ? $" {keyEvent.Key}" : "", keyEvent.Modifiers);

            // a hotkey consumes its downs and repeats; an up is forwarded only if its down went out (see AdmitKey), so a key
            // held before the chord formed is still released on the slave
            var hotkeyAction = profile.Hotkeys.Match(keyEvent);
            var hotkeyConsumed = hotkeyAction.HasValue;
            // !IsRepeat: an auto-repeat of a held hotkey must not re-fire the toggle every tick
            if (hotkeyConsumed && keyEvent.Type == KeyEventType.KeyDown && !keyEvent.IsRepeat)
            {
                if (hotkeyAction == HotkeyAction.ToggleCursorLock)
                {
                    // an explicit lock or unlock says where the cursor belongs, so a pending screensaver restore is off
                    st.SavedScreenName = null;
                    // a remote-only master with no local screen has nowhere to return to, so confine the
                    // cursor to the current remote screen instead
                    if (profile.RemoteOnly && !st.Screens.Any(sc => sc.IsLocal))
                    {
                        st.ConfinedToScreen = !st.ConfinedToScreen;
                        ShowOsd(st, st.ConfinedToScreen ? "Cursor lock: On" : "Cursor lock: Off");
                        if (log.IsEnabled(LogLevel.Information))
                            log.LogInformation("Cursor lock: {State} (remote-only, no local screen)",
                                st.ConfinedToScreen ? "confined to current screen" : "free to roam");
                    }
                    else
                    {
                        st.LockedToScreen = !st.LockedToScreen;
                        if (profile.RemoteOnly)
                        {
                            // ShowOsd falls back to a local osd off a remote screen, and a remote-only master
                            // often has no display, so each branch shows its OSD while a remote screen is
                            // current: after entering when locking, before leaving when unlocking
                            if (st.LockedToScreen)
                            {
                                log.LogInformation("Remote lock: locked to remote");
                                if (await TryEnterRemoteOnly(st)) ShowOsd(st, "Input: remote");
                            }
                            else
                            {
                                log.LogInformation("Remote lock: unlocked (local)");
                                if (st.Mouse.IsOnVirtualScreen)
                                {
                                    ShowOsd(st, "Input: local");
                                    ReturnHome(st, null, slaveReachable: true);
                                }
                            }
                        }
                        else
                        {
                            ShowOsd(st, st.LockedToScreen ? "Mouse lock: On" : "Mouse lock: Off");
                            if (log.IsEnabled(LogLevel.Information))
                                log.LogInformation("Screen lock: {State}", st.LockedToScreen ? "locked" : "unlocked");
                        }
                    }
                }
                else if (hotkeyAction == HotkeyAction.LockSlaves)
                {
                    // Lock every connected slave on demand. On a remote-only master this is the only
                    // route to BroadcastLockScreen: that is otherwise driven by this machine's own
                    // ScreenLocked event, which is Mac/Windows-only and cannot fire on a headless box.
                    // Deliberately not gated on screenLockPropagation - that flag governs automatic
                    // propagation, and an explicit keypress should never silently do nothing.
                    ShowOsd(st, "Locking slaves");
                    log.LogInformation("Lock hotkey: locking all slaves");
                    await BroadcastLockScreen(st);
                }
                else if (hotkeyAction == HotkeyAction.ToggleRelativeMouse && st.Mouse.IsOnVirtualScreen)
                {
                    var screenName = st.Mouse.CurrentScreen.Name;
                    var isNowRelative = !st.RelativeMouseScreens.GetValueOrDefault(screenName);
                    st.RelativeMouseScreens[screenName] = isNowRelative;
                    if (log.IsEnabled(LogLevel.Information))
                        log.LogInformation("Mouse mode for '{Screen}': {Mode}", screenName, isNowRelative ? "relative" : "absolute");
                    ShowOsd(st, isNowRelative ? "Relative mouse: On" : "Relative mouse: Off");
                }
                else if (hotkeyAction == HotkeyAction.CopyFiles)
                {
                    if (_fileTransfer.FileTransferOngoing)
                    {
                        ShowOsd(st, FileTransferService.InProgressOsd);
                    }
                    else if (!st.Mouse.IsOnVirtualScreen)
                    {
                        if (!_selectionDetector.IsFileTransferSupported)
                        {
                            log.LogInformation("Copy hotkey: file transfer not supported on this platform");
                            ShowOsd(st, FileTransferService.NotSupportedOsd);
                        }
                        else
                        {
                            CopyLocalSelection(st);
                        }
                    }
                    else if (st.Mouse.IsOnVirtualScreen && relay.IsConnected)
                    {
                        if (log.IsEnabled(LogLevel.Information))
                            log.LogInformation("Copy hotkey: querying file selection on {Host}", st.Mouse.CurrentScreen.Host);
                        CopyRemoteSelection(st, st.Mouse.CurrentScreen.Host);
                        relay.Send(st.Mouse.CurrentScreen.Host, MessageKind.FileSelectionQuery, new FileSelectionQueryMessage());
                    }
                }
                else if (hotkeyAction == HotkeyAction.MissionControl && st.Mouse.IsOnVirtualScreen)
                {
                    if (log.IsEnabled(LogLevel.Information))
                        log.LogInformation("Mission Control hotkey: sending to {Host}", st.Mouse.CurrentScreen.Host);
                    var host = st.Mouse.CurrentScreen.Host;
                    relay.SendKeyEvent([host], new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.None, null, SpecialKey.MissionControl));
                    relay.SendKeyEvent([host], new KeyEventMessage(KeyEventType.KeyUp, KeyModifiers.None, null, SpecialKey.MissionControl));
                }
                else if (hotkeyAction == HotkeyAction.PasteFiles)
                {
                    if (_fileTransfer.FileTransferOngoing)
                    {
                        ShowOsd(st, FileTransferService.InProgressOsd);
                    }
                    else if (!_selectionDetector.IsFileTransferSupported)
                    {
                        log.LogInformation("Paste hotkey: file transfer not supported on this platform");
                        ShowOsd(st, FileTransferService.NotSupportedOsd);
                    }
                    else if (st.CopyPending)
                    {
                        log.LogInformation("Paste hotkey: the latest copy is still waiting for the selection");
                        ShowOsd(st, FileSelectionResult.InProgressMessage);
                    }
                    else if (_fileTransfer.GetCopyBuffer() is { } copyBuffer)
                    {
                        var targetHost = st.Mouse.IsOnVirtualScreen
                            ? st.Mouse.CurrentScreen.Host
                            : profile.Name;
                        if (copyBuffer.SourceHost.EqualsIgnoreCase(targetHost))
                        {
                            if (log.IsEnabled(LogLevel.Information))
                                log.LogInformation("Paste hotkey: source and target are the same host ({Host}), nothing to do", targetHost);
                            ShowOsd(st, FileTransferService.InvalidTargetOsd);
                        }
                        else
                        {
                            if (log.IsEnabled(LogLevel.Information))
                                log.LogInformation("Paste hotkey: {Count} {Files} from {Source} → {Target}",
                                    copyBuffer.Paths.Length, Pluralize.Of(copyBuffer.Paths.Length, "file"), copyBuffer.SourceHost, targetHost);
                            if (!_fileTransfer.InitiatePaste(copyBuffer, targetHost, profile.Name, relay))
                                SendOsd(targetHost, FileTransferService.InvalidTargetOsd);
                        }
                    }
                    else
                    {
                        log.LogInformation("Paste hotkey: copy buffer is empty");
                        ShowOsd(st, "Nothing to paste");
                    }
                }
            }

            if ((!hotkeyConsumed || keyEvent.Type == KeyEventType.KeyUp) && st.Mouse.IsOnVirtualScreen && relay.IsConnected && AdmitKey(st, keyEvent))
            {
                // repeats are master-driven: each OS auto-repeat is re-resolved (live modifier/dead-key state)
                // and forwarded with IsRepeat set, so the slave injects the correct character every tick.
                ForwardKeyToVirtualScreen(st,
                    new KeyEventMessage(keyEvent.Type, keyEvent.Modifiers, keyEvent.Character, RemapKey(keyEvent.Key), IsRepeat: keyEvent.IsRepeat));
            }
        });
    }

    // A repeat or up is forwarded only for a key whose down this visit forwarded, so a key held across the edge
    // never reaches the slave (Deskflow's behaviour): it is re-pressed there, and released where it was pressed.
    private static bool AdmitKey(LocalMasterState st, KeyEvent keyEvent)
    {
        if (keyEvent.Type == KeyEventType.KeyUp) return st.ForwardedKeys.Remove(keyEvent.PhysicalKey);
        if (keyEvent.IsRepeat) return st.ForwardedKeys.Contains(keyEvent.PhysicalKey);
        st.ForwardedKeys.Add(keyEvent.PhysicalKey);
        return true;
    }

    private void OnMouseButton(MouseButtonEvent e)
    {
        SealMouseBatch();
        _ = _commands.Writer.TryWrite(async st =>
        {
            st.LastInputTick = _getTickCount();
            st.EdgeRefusalLogged = false;
            await activityTracker.LocalActivity();
            if (st.Mouse.IsOnVirtualScreen && relay.IsConnected)
            {
                if (log.IsEnabled(LogLevel.Debug))
                    log.LogDebug("Mouse: {Type} {Button}", e.IsPressed ? "down" : "up", e.Button);
                // several physical buttons share Extra2, so releasing one of two lets a crossing through; the slave
                // cannot tell them apart either
                if (e.IsPressed) st.ForwardedButtons.Add(e.Button);
                else st.ForwardedButtons.Remove(e.Button);
                ForwardToVirtualScreen(st, MessageKind.MouseButton, new MouseButtonMessage(e.Button, e.IsPressed));
            }
            // a restore or remote-only entry refused for a held button is retried as it comes up
            else if (!e.IsPressed && !await TryRestoreAfterScreensaver(st))
            {
                await TryEnterRemoteOnly(st);
            }
        });
    }

    private void OnMouseScroll(MouseScrollEvent e)
    {
        SealMouseBatch();
        _ = _commands.Writer.TryWrite(async st =>
        {
            st.LastInputTick = _getTickCount();
            await activityTracker.LocalActivity();
            if (st.Mouse.IsOnVirtualScreen && relay.IsConnected)
            {
                if (log.IsEnabled(LogLevel.Debug))
                    log.LogDebug("Scroll: x={X} y={Y}", e.XDelta, e.YDelta);
                ForwardToVirtualScreen(st, MessageKind.MouseScroll, new MouseScrollMessage(e.XDelta, e.YDelta));
            }
        });
    }

    private void SendMousePosition(LocalMasterState st, long now)
    {
        if (!relay.IsConnected || !st.Mouse.IsOnVirtualScreen) return;

        var screen = st.Mouse.CurrentScreen;

        if (st.RelativeMouseScreens.GetValueOrDefault(screen.Name))
        {
            // relative mode: send accumulated delta, preserve sub-pixel remainders
            var intDx = (int)st.PendingDx;
            var intDy = (int)st.PendingDy;
            if (intDx == 0 && intDy == 0) return;
            st.PendingDx -= intDx;
            st.PendingDy -= intDy;
            if (profile.DebugMouse && log.IsEnabled(LogLevel.Information))
                log.LogInformation("[mouse] delta to {Host}: dx={Dx} dy={Dy}", screen.Host, intDx, intDy);
            st.LastMouseSendTick = now;
            relay.SendMouseDelta([screen.Host], intDx, intDy);
            return;
        }

        // absolute mode: send current virtual position, discard accumulated deltas
        st.PendingDx = 0;
        st.PendingDy = 0;
        if (profile.DebugMouse && log.IsEnabled(LogLevel.Information))
            log.LogInformation("[mouse] move to {Host}: screen={Screen} x={X} y={Y}", screen.Host, screen.Name, (int)st.Mouse.X, (int)st.Mouse.Y);
        st.LastMouseSendTick = now;
        relay.Send(screen.Host, MessageKind.MouseMove, new MouseMoveMessage(screen.Name, (int)st.Mouse.X, (int)st.Mouse.Y));
    }

    // flush pending state before leaving a virtual screen.
    // relative mode: send any accumulated delta.
    // absolute mode: always send current position so slave cursor doesn't lag at exit point.
    private void FlushMouseDelta(LocalMasterState st)
    {
        if (!relay.IsConnected || !st.Mouse.IsOnVirtualScreen) return;
        var screen = st.Mouse.CurrentScreen;
        var isRelative = st.RelativeMouseScreens.GetValueOrDefault(screen.Name);
        if (isRelative && st.PendingDx == 0 && st.PendingDy == 0) return;
        SendMousePosition(st, _getTickCount());
    }

    // remap Home/End to platform-independent line-nav keys when master is not Mac.
    private static SpecialKey? RemapKey(SpecialKey? key) => key switch
    {
        SpecialKey.Home when !OperatingSystem.IsMacOS() => SpecialKey.MoveToBeginningOfLine,
        SpecialKey.End when !OperatingSystem.IsMacOS() => SpecialKey.MoveToEndOfLine,
        _ => key,
    };

    private void ForwardToVirtualScreen<T>(LocalMasterState st, MessageKind kind, T message)
    {
        var target = st.Mouse.CurrentScreen?.Host;
        if (target == null) return;
        relay.Send(target, kind, message);
    }

    /// <summary>
    /// The key twin of <see cref="ForwardToVirtualScreen{T}"/>, handing the event over TYPED so the relay
    /// can append it to an open bundle without decoding a payload we would only just have encoded.
    /// </summary>
    private void ForwardKeyToVirtualScreen(LocalMasterState st, KeyEventMessage message)
    {
        var target = st.Mouse.CurrentScreen?.Host;
        if (target == null) return;
        relay.SendKeyEvent([target], message);
    }

    private void OnMouseMove(double x, double y)
    {
        PostMouseInput(MouseInputKind.Absolute, x, y);
    }

    private async ValueTask HandleRealScreenMove(LocalMasterState st, double x, double y)
    {
        // track which local screen the cursor is on
        var screen = FindLocalScreenAt(st, (int)x, (int)y) ?? st.ActiveLocalScreen!;
        if (screen != st.ActiveLocalScreen)
        {
            st.ActiveLocalScreen = screen;
            UpdateWarpPoint(st, screen);
        }

        // a restore or remote-only entry refused for a held button is retried here too: its release can be handled
        // before the OS has the button up
        if (await TryRestoreAfterScreensaver(st) || await TryEnterRemoteOnly(st)) return;
        if (st.LockedToScreen) return;

        var localX = (int)x - screen.X;
        var localY = (int)y - screen.Y;
        var hit = st.Layout!.DetectEdgeExit(screen, localX, localY);
        if (hit is null) return;
        if (!relay.IsConnected) return;

        if (!await EnterFromLocal(st, hit.Destination, hit.Entry.X, hit.Entry.Y, hit.Exit)) return;
        if (log.IsEnabled(LogLevel.Information))
            log.LogInformation("Entered remote screen '{Name}' → ({X}, {Y})", hit.Destination.Name, hit.Entry.X, hit.Entry.Y);
    }

    private async ValueTask HandleVirtualScreenMove(LocalMasterState st, double x, double y)
    {
        var dx = x - st.LastWarpX;
        var dy = y - st.LastWarpY;

        // zero-delta filter (also catches Mac/Windows warp events which arrive at WarpX,WarpY)
        if (dx == 0 && dy == 0) return;

        // bogus filter: drop delta that looks like a warp-displacement artifact
        if (Math.Abs(dx) > st.HalfW - 10 || Math.Abs(dy) > st.HalfH - 10) return;

        var prevScreen = st.Mouse.ApplyDelta(dx, dy);
        if (prevScreen != null)
            HandleIntraHostTransition(st);
        else
            AccumulateScaled(st, dx, dy);

        var now = _getTickCount();
        if (now - st.LastVirtualLogTick >= 100)
        {
            st.LastVirtualLogTick = now;
            if (st.Mouse.IsOnVirtualScreen && st.RelativeMouseScreens.GetValueOrDefault(st.Mouse.CurrentScreen.Name) && log.IsEnabled(LogLevel.Debug))
                log.LogDebug("Mouse: ({X}, {Y})  Offset: ({DX}, {DY})", (int)st.Mouse.X, (int)st.Mouse.Y, (int)st.PendingDx, (int)st.PendingDy);
            else if (log.IsEnabled(LogLevel.Debug))
                log.LogDebug("Mouse: ({X}, {Y})", (int)st.Mouse.X, (int)st.Mouse.Y);
        }

        var leavingScreen = st.Mouse.CurrentScreen;
        if (leavingScreen != null && await TryHandleEdgeExit(st, leavingScreen)) return;

        SendMousePositionIfDue(st, now);
        if (!platform.RecentresItself) Recenter(st);
    }

    private void HandleIntraHostTransition(LocalMasterState st)
    {
        st.PendingDx = 0;
        st.PendingDy = 0;
        if (!relay.IsConnected || !st.Mouse.IsOnVirtualScreen) return;
        SendEnterMessage(st.Mouse.CurrentScreen, (int)st.Mouse.X, (int)st.Mouse.Y);
    }

    // Every return to the local screen, on the consumer. The cursor is shown (or hidden) before the flag clears and
    // it warps: the cursor hider's pin to the park point waits for Show/Hide, not for the warp. No warpTo leaves the
    // cursor where it is. A slave that is gone (it left, or the relay did) is neither flushed to nor told. Returns the
    // host left, or null when already local.
    private string? ReturnHome(LocalMasterState st, ScreenPoint? warpTo, bool slaveReachable)
    {
        if (!st.Mouse.IsOnVirtualScreen) return null;
        var host = st.Mouse.CurrentScreen.Host;
        if (slaveReachable) FlushMouseDelta(st);
        st.Mouse.LeaveScreen();
        lock (_hashLock)
            _cursorHost = null;
        st.PendingDx = 0;
        st.PendingDy = 0;
        ShowCursorOnReturn();
        platform.ReturnTo(warpTo);
        LeaveHost(st, host, slaveReachable);
        return host;
    }

    // Ends the visit to a host the cursor is leaving: releases the buttons pressed there, then tells it to leave. Both
    // go to that host by name, since the cursor has already moved off it.
    private void LeaveHost(LocalMasterState st, string host, bool reachable)
    {
        lock (_hashLock)
        {
            // before LeaveScreen goes: a hash read for this visit finishing from here on is dropped
            if (host.EqualsIgnoreCase(_cursorHost)) _cursorHost = null;
            // an owed read goes wherever the cursor is, so leaving does not outdate it
            if (!_owedHashes.TryGetValue(host, out var owed) || owed != _hashTokens.GetValueOrDefault(host)) NextHashToken(host);
        }
        if (reachable && relay.IsConnected)
        {
            foreach (var button in st.ForwardedButtons)
                relay.Send(host, MessageKind.MouseButton, new MouseButtonMessage(button, false));
            LeaveRemoteScreen(host);
        }
        st.ForgetForwarded();
    }

    // where a return with no edge of its own (a disconnect, the screensaver) puts the cursor: back through the edge the
    // visit left by, like an ordinary return; else (no edge, or its screen is gone) the park point
    private static ScreenPoint LocalLandingPoint(LocalMasterState st) =>
        st.ExitEdge is { } exit && ScreenLayout.LandingBack(exit, st.LocalScreens) is { } landing ? landing : new ScreenPoint(st.WarpX, st.WarpY);

    private async ValueTask<RemoteScreenInfo> RemoteInfoFor(LocalMasterState st, ScreenRect dest) =>
        GetRemoteScreensAndScales(st.Screens, await _peerState.GetPeerScreensSnapshot(), dest);

    // Every entry from the local screen to a remote one; callers keep their own gates and log line. exitEdge is what
    // a return with no edge of its own comes back through (see LocalLandingPoint), null when the entry had no edge.
    // Refused (false) while a mouse button is held: it went down here, and its up would be swallowed during the visit.
    private async ValueTask<bool> EnterFromLocal(LocalMasterState st, ScreenRect dest, int x, int y, ScreenEdge? exitEdge)
    {
        if (platform.AnyMouseButtonHeld()) return false;
        st.SavedScreenName = null;
        st.ExitEdge = exitEdge;
        var remoteInfo = await RemoteInfoFor(st, dest);
        cursorHider.Hide();
        platform.IsOnVirtualScreen = true;
        st.ForgetForwarded();
        ApplyEnterScreen(st, dest, remoteInfo, x, y);
        await ParkOnEntry(st);
        SendEnterScreen(dest, x, y);
        return true;
    }

    // hides or shows the cursor when control returns to the local screen:
    // with hideCursor, restart the inactivity timer instead of showing unconditionally
    private void ShowCursorOnReturn()
    {
        if (profile.HideCursor) cursorHider.Hide();
        else cursorHider.Show();
    }

    // enters remote-only mode targeting the first remote screen with known dimensions; must be called from consumer.
    // returns whether it entered. It does nothing when not remote-only, already virtual or unlocked, during the
    // screensaver (its end restores the visit, or enters here), or with no screen or relay ready; while a mouse button
    // is held it is refused, and retried on the button's release and on local moves.
    private async ValueTask<bool> TryEnterRemoteOnly(LocalMasterState st)
    {
        if (!profile.RemoteOnly || st.Mouse.IsOnVirtualScreen || st.ScreensaverActive) return false;
        if (!st.LockedToScreen) return false;  // user explicitly unlocked to local — don't auto-re-enter
        var target = st.Screens.FirstOrDefault(s => !s.IsLocal && s.Width > 0);
        if (target == null) return false;
        if (!relay.IsConnected) return false;

        var entryX = target.Width / 2;
        var entryY = target.Height / 2;

        if (!await EnterFromLocal(st, target, entryX, entryY, null)) return false;
        st.LockedToScreen = true;
        if (log.IsEnabled(LogLevel.Information))
            log.LogInformation("Remote-only: entered '{Name}' → ({X}, {Y})", target.Name, entryX, entryY);
        return true;
    }

    // handles raw mouse deltas — used by evdev (remote-only) and Xorg (local master, virtual screen).
    // feeds directly into VirtualMouseState — no warp-point math needed.
    private void OnMouseDelta(double dx, double dy)
    {
        PostMouseInput(MouseInputKind.Delta, dx, dy);
    }

    private void PostMouseInput(MouseInputKind kind, double x, double y)
    {
        lock (_mouseBatchLock)
        {
            // a different kind cannot coalesce into the open batch — release that one and start over
            if (_openMouseBatch != null && !_openMouseBatch.Matches(kind))
                PostOpenMouseBatch();

            _openMouseBatch ??= new CoalescingBatch<MouseInputKind, double>(kind, accumulate: kind == MouseInputKind.Delta);
            _openMouseBatch.Add(x, y);

            // Raw samples arrive at the mouse's polling rate, commonly up to 1000/s, and posting
            // an actor command each costs a thread-pool wake per sample while buying nothing, since
            // sends are throttled to the configured rate anyway. Holding the batch for the rest of the interval
            // loses no movement: deltas accumulate and absolute samples keep the latest.
            var due = _minMouseIntervalMs - (_getTickCount() - _lastMouseBatchPostTick);
            if (due <= 0)
                PostOpenMouseBatch();
            else
                ArmMouseFlush(due);
        }
    }

    // Releases the open batch so the command queued after it cannot overtake motion already captured.
    private void SealMouseBatch()
    {
        lock (_mouseBatchLock) PostOpenMouseBatch();
    }

    // caller holds _mouseBatchLock
    private void PostOpenMouseBatch()
    {
        var batch = _openMouseBatch;
        if (batch == null) return;
        _openMouseBatch = null;
        _lastMouseBatchPostTick = _getTickCount();
        if (!_commands.Writer.TryWrite(st => ProcessMouseBatch(st, batch))) return;
        Interlocked.Increment(ref _postedMouseBatchCount);
    }

    // caller holds _mouseBatchLock
    private void ArmMouseFlush(long dueMs)
    {
        if (_mouseFlushArmed) return;
        _mouseFlushArmed = true;
        _mouseFlushTimer?.Change(TimeSpan.FromMilliseconds(dueMs), Timeout.InfiniteTimeSpan);
    }

    private void FlushMouseBatch()
    {
        lock (_mouseBatchLock)
        {
            _mouseFlushArmed = false;
            PostOpenMouseBatch();
        }
    }

    private async ValueTask ProcessMouseBatch(LocalMasterState st, CoalescingBatch<MouseInputKind, double> batch)
    {
        double x, y;
        var kind = batch.Kind;
        lock (_mouseBatchLock)
            (x, y) = batch.Snapshot();

        st.LastInputTick = _getTickCount();
        await activityTracker.LocalActivity();

        if (kind == MouseInputKind.Absolute)
        {
            if (st.Layout is null || st.ActiveLocalScreen is null) return;
            if (!st.Mouse.IsOnVirtualScreen)
                await HandleRealScreenMove(st, x, y);
            else
                await HandleVirtualScreenMove(st, x, y);
            return;
        }

        await HandleMouseDelta(st, x, y);
    }

    private async ValueTask HandleMouseDelta(LocalMasterState st, double dx, double dy)
    {
        if (!st.Mouse.IsOnVirtualScreen) return;

        var leavingScreen = st.Mouse.CurrentScreen!;
        var prevScreen = st.Mouse.ApplyDelta(dx, dy);
        if (prevScreen != null)
        {
            HandleIntraHostTransition(st);
            if (!platform.RecentresItself) Recenter(st);
            return;
        }

        if (await TryHandleEdgeExit(st, leavingScreen)) return;

        AccumulateScaled(st, dx, dy);
        SendMousePositionIfDue(st, _getTickCount());

        if (!platform.RecentresItself) RecenterIfDrifted(st, dx, dy);
    }

    // Crosses to the edge's neighbour, unless locked or a button pressed on this host is held. Confinement holds a
    // remote-only master on its host, and the lock holds any other master on its screen; moves between one host's own
    // monitors happen inside ApplyDelta and are unaffected. Returns true when the cursor left the screen.
    private async ValueTask<bool> TryHandleEdgeExit(LocalMasterState st, ScreenRect leavingScreen)
    {
        var hit = st.Layout?.DetectEdgeExit(leavingScreen, (int)st.Mouse.X, (int)st.Mouse.Y);
        if (hit is null) return false;

        if (hit.Destination.IsLocal)
        {
            if (st.LockedToScreen || profile.RemoteOnly || ButtonHoldsHost(st, leavingScreen, hit)) return false;
            ReturnAtEdge(st, hit);
            return true;
        }

        if (profile.RemoteOnly ? st.ConfinedToScreen : st.LockedToScreen)
        {
            if (log.IsEnabled(LogLevel.Debug))
                log.LogDebug("Cursor lock: blocked transition to '{Name}'", hit.Destination.Name);
            return false;
        }

        if (!relay.IsConnected || ButtonHoldsHost(st, leavingScreen, hit)) return false;
        await CrossToRemote(st, leavingScreen, hit);
        return true;
    }

    private static bool ChangesHost(ScreenRect leavingScreen, EdgeHit hit) =>
        hit.Destination.IsLocal || hit.Destination.Host != leavingScreen.Host;

    // A button pressed on a remote keeps the cursor on that host until its up, so the up reaches the host with its
    // down. When the platform sees every held button despite its tap swallowing them it is asked too, so an up that
    // never arrived cannot hold the cursor there; it is asked only mid-drag, since on Xorg that is a server round-trip.
    private bool ButtonHoldsHost(LocalMasterState st, ScreenRect leavingScreen, EdgeHit hit)
    {
        if (!ChangesHost(leavingScreen, hit) || st.ForwardedButtons.Count == 0) return false;
        if (st.ForwardedButtons.All(platform.SeesSwallowed) && !platform.AnyMouseButtonHeld()) return false;
        if (!st.EdgeRefusalLogged && log.IsEnabled(LogLevel.Information))
            log.LogInformation("Edge refused: {Buttons} held on {Host}", string.Join(", ", st.ForwardedButtons), leavingScreen.Host);
        st.EdgeRefusalLogged = true;
        return true;
    }

    private void ReturnAtEdge(LocalMasterState st, EdgeHit hit)
    {
        var targetScreen = hit.Destination;
        var landing = targetScreen.ToGlobal(hit.Entry);
        ReturnHome(st, landing, slaveReachable: true);
        st.ActiveLocalScreen = targetScreen;
        UpdateWarpPoint(st, targetScreen);
        if (log.IsEnabled(LogLevel.Information))
            log.LogInformation("Returned to local screen ← ({X}, {Y})", landing.X, landing.Y);
    }

    private async ValueTask CrossToRemote(LocalMasterState st, ScreenRect leavingScreen, EdgeHit hit)
    {
        FlushMouseDelta(st);
        ApplyEnterScreen(st, hit.Destination, await RemoteInfoFor(st, hit.Destination), hit.Entry.X, hit.Entry.Y);
        if (log.IsEnabled(LogLevel.Information))
            log.LogInformation("Switched to remote screen '{Name}' → ({X}, {Y})", hit.Destination.Name, hit.Entry.X, hit.Entry.Y);
        if (ChangesHost(leavingScreen, hit)) LeaveHost(st, leavingScreen.Host, reachable: true);
        SendEnterScreen(hit.Destination, hit.Entry.X, hit.Entry.Y);
    }

    // throttles mouse sends to the configured rate
    private void SendMousePositionIfDue(LocalMasterState st, long now)
    {
        if (now - st.LastMouseSendTick >= _minMouseIntervalMs)
            SendMousePosition(st, now);
    }

    private enum MouseInputKind { Absolute, Delta }

    // One accumulator serves both modes: relative sends it as a delta and keeps the sub-pixel
    // remainder, absolute discards it and sends the virtual position instead. Resolving the scale
    // here is what keeps both capture surfaces to a single expression with no mode knowledge.
    private static void AccumulateScaled(LocalMasterState st, double dx, double dy)
    {
        var isRelative = st.RelativeMouseScreens.GetValueOrDefault(st.Mouse.CurrentScreen!.Name);
        var scale = isRelative ? (double)(st.Mouse.RelativeMouseScale ?? st.Mouse.MouseScale) : (double)st.Mouse.MouseScale;
        st.PendingDx += dx * scale;
        st.PendingDy += dy * scale;
    }

    // Parks the cursor for the start of a visit. The anchor is set at once so pre-queued events compute a large
    // delta, caught by the bogus filter; the physical warp waits until the shield is absorbing, so nothing hovers
    // at the park point. A headless master has no local screen, so nothing to park.
    private async ValueTask ParkOnEntry(LocalMasterState st)
    {
        if (st.ActiveLocalScreen == null) return;
        AnchorAtWarpPoint(st);
        await platform.WarpToPark(st.WarpX, st.WarpY);
    }

    // Re-anchors the delta reference on the warp point. Every place that physically parks the cursor
    // there owes this, or the next sample measures its delta from where the cursor used to be.
    private static void AnchorAtWarpPoint(LocalMasterState st)
    {
        st.LastWarpX = st.WarpX;
        st.LastWarpY = st.WarpY;
        st.DriftX = 0;
        st.DriftY = 0;
    }

    // warps to centre and re-anchors the drift reference
    private void Recenter(LocalMasterState st)
    {
        if (st.ActiveLocalScreen == null) return;
        platform.WarpCursor(st.WarpX, st.WarpY);
        AnchorAtWarpPoint(st);
    }

    // XI_RawMotion is raw hardware motion with no on-screen position, so it can never run into an edge: warp only
    // once the drift leaves the dead zone
    private void RecenterIfDrifted(LocalMasterState st, double dx, double dy)
    {
        if (st.ActiveLocalScreen == null) return;
        st.DriftX += dx;
        st.DriftY += dy;

        if (Math.Abs(st.DriftX) < st.HalfW * WarpDeadZone && Math.Abs(st.DriftY) < st.HalfH * WarpDeadZone)
        {
            // no warp, so the reference is wherever the cursor actually ended up
            st.LastWarpX = st.WarpX + st.DriftX;
            st.LastWarpY = st.WarpY + st.DriftY;
            return;
        }

        Recenter(st);
    }

    // updates mouse state when entering a remote screen; always called before sending relay messages
    private static void ApplyEnterScreen(LocalMasterState st, ScreenRect dest, RemoteScreenInfo remoteInfo, int entryX, int entryY)
    {
        var scale = remoteInfo.ScaleMap.GetValueOrDefault(dest.Name, 1.0m);
        st.Mouse.EnterScreen(dest, remoteInfo.Screens, entryX, entryY, scale, remoteInfo.ScaleMap, remoteInfo.RelativeScaleMap);
        st.PendingDx = 0;
        st.PendingDy = 0;
    }

    // sends EnterScreen relay message + pushes clipboard to destination host
    private void SendEnterScreen(ScreenRect dest, int entryX, int entryY)
    {
        bool owed;
        lock (_hashLock)
        {
            // a new visit: a hash read for an earlier one, even one this entry defers, is outdated; an owed one's hash is
            // owed by the entry's instead
            owed = _owedHashes.ContainsKey(dest.Host);
            NextHashToken(dest.Host);
            _visits[dest.Host] = ++_visitIds;
            _cursorHost = dest.Host;
        }
        SendEnterMessage(dest, entryX, entryY);
        OfferClipboardOnEntry(dest.Host, owed);
    }

    private void SendEnterMessage(ScreenRect dest, int entryX, int entryY) =>
        relay.Send(dest.Host, MessageKind.EnterScreen, new EnterScreenMessage(dest.Name, entryX, entryY, dest.Width, dest.Height));

    // sends LeaveScreen relay message + pulls clipboard from host (unconditional — callers guard the condition)
    private void LeaveRemoteScreen(string host)
    {
        relay.Send(host, MessageKind.LeaveScreen, new LeaveScreenMessage());
        PullClipboardFromHost(host);
    }

    private bool UseHydraClipboard(string host) => UseHydraClipboard(profile.ClipboardSync, _localPlatform, _peerState.PeerPlatformOf(host));

    internal static bool UseHydraClipboard(ClipboardSyncMode mode, PeerPlatform localPlatform, PeerPlatform remotePlatform) =>
        mode != ClipboardSyncMode.System || localPlatform != PeerPlatform.MacOs || remotePlatform != PeerPlatform.MacOs;

    private void UpdateWarpPoint(LocalMasterState st, ScreenRect screen)
    {
        st.HalfW = screen.Width / 2;
        st.HalfH = screen.Height / 2;
        st.WarpX = screen.X + st.HalfW;
        st.WarpY = screen.Y + st.HalfH;
        st.DriftX = 0;
        st.DriftY = 0;
        cursorHider.UpdateWarpPoint(st.WarpX, st.WarpY);
    }

    private static ScreenRect? FindLocalScreenAt(LocalMasterState st, int x, int y) =>
        st.LocalScreens.FirstOrDefault(s => s.Contains(x, y));

    private void LogDetectedScreens(List<ScreenRect> detected)
    {
        if (!log.IsEnabled(LogLevel.Information)) return;
        log.LogInformation("Detected {Count} local {Screens}:", detected.Count, Pluralize.Of(detected.Count, "screen"));
        for (var i = 0; i < detected.Count; i++)
            if (detected[i].Identity != null)
                log.LogInformation("  Screen {I}: {Json}", i, JsonSerializer.Serialize(detected[i].Identity, ScreenDetector.JsonOptions));
    }

    // combines local screens with placeholder remote screens (Width=0 until ScreenInfo arrives)
    private List<ScreenRect> BuildAllScreens(List<ScreenRect> localScreens)
    {
        var result = new List<ScreenRect>(localScreens);
        foreach (var host in profile.RemoteHosts)
            result.Add(new ScreenRect(host.Name, host.Name, 0, 0, 0, 0, IsLocal: false));
        return result;
    }

    private class LocalMasterState
    {
        public List<ScreenRect> Screens = [];
        public List<ScreenRect> LocalScreens = [];
        public List<ScreenInfoEntry> LocalScreenEntries = [];
        public ScreenRect? ActiveLocalScreen;
        public ScreenLayout? Layout;
        public VirtualMouseState Mouse = new();
        public int WarpX, WarpY, HalfW, HalfH;
        public double LastWarpX, LastWarpY;
        public double DriftX, DriftY;
        public long LastVirtualLogTick;
        public bool LockedToScreen;
        // remote-only with no local screen: confine the cursor to the current remote screen.
        // separate from LockedToScreen, which in remote-only means 'input is on remote at all'
        // and defaults to true - reusing it would confine the cursor by default.
        public bool ConfinedToScreen;

        // keys whose down this visit forwarded, see AdmitKey. every entry from local and every new host starts it
        // empty, since a slave releases everything at LeaveScreen; moving between one host's monitors keeps it
        public HashSet<int> ForwardedKeys = [];

        // buttons whose down this visit forwarded and whose up has not, see ButtonHoldsHost. LeaveHost sends their ups
        // ahead of LeaveScreen, so the slave's own release-all finds nothing left to release
        public HashSet<MouseButton> ForwardedButtons = [];

        // whether the current button state has already logged a refused edge; any button event resets it
        public bool EdgeRefusalLogged;

        public void ForgetForwarded()
        {
            ForwardedKeys.Clear();
            ForwardedButtons.Clear();
        }

        // where the visit left the local screen, see LocalLandingPoint
        public ScreenEdge? ExitEdge;

        // per-screen relative mouse mode (true = relative, false/absent = absolute)
        public Dictionary<string, bool> RelativeMouseScreens = new(StringComparer.OrdinalIgnoreCase);

        // screensaver sync: saved cursor location before screensaver snap-back, kept while its restore is pending (see
        // TryRestoreAfterScreensaver)
        public bool ScreensaverActive;
        public string? SavedScreenName;
        public int SavedCursorX, SavedCursorY;

        // send throttle: accumulated deltas and last send time
        public long LastMouseSendTick;
        public double PendingDx;
        public double PendingDy;

        // last time any input event was processed, regardless of destination (local or remote)
        public long LastInputTick;

        // copy hotkey: press count, whether the latest press is still waiting for the selection, and the host it
        // is waiting on if it was a remote one
        public long CopyGeneration;
        public bool CopyPending;
        public string? RemoteCopyHost;
    }

    private record RemoteScreenInfo(List<ScreenRect> Screens, Dictionary<string, decimal> ScaleMap, Dictionary<string, decimal?> RelativeScaleMap);

    private enum RemoteSelectionOutcome
    {
        Applied,
        Superseded,
        NotApplied,
    }
}
