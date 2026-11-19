using Hydra.Config;
using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Screen;
using Microsoft.Extensions.Logging;
using Tmds.DBus.Protocol;

namespace Hydra.Platform.Linux.Wayland;

// The master's input on Wayland, as Deskflow does it: the InputCapture portal puts pointer barriers on the local edges a
// crossing can start from, and a hit hands every key and pointer event to our EIS receiver until we release it. A hit
// reaches the router as an absolute move onto the edge pixel, tagged with its serial (IBarrierHits); it then either enters
// a remote (IsOnVirtualScreen) or says StayedHome for that serial, and the capture is released; one the router leaves
// unanswered is released by a watchdog. Release is the only way to place the pointer, so the return's warp rides on it.
// Portal calls run off the router's actor and the EIS and D-Bus threads in two bounded lanes: releases in one, so they
// never wait behind barrier work in the other. Barrier work waits for a capture to be
// released: setting barriers disables the session. At home no key reaches EIS, so the hotkeys that act there are bound as
// global shortcuts, pressed as their chord's keys.
internal sealed class WaylandInputHandler(ICaptureSession session, IHydraProfile profile, ICaptureClipboard? clipboard, TimeProvider time, ILogger log,
    // tests: useKeymaps false builds no key resolver, so no xkbcommon is loaded; no key then reaches the router
    bool useKeymaps = true,
    // xdg-desktop-portal-hyprland's capture, which stops sending input after a button up it never sent the down of
    bool hyprland = false,
    // the kernel's keyboards, read for the hotkeys at home in place of the portal's shortcuts when any is readable
    IEvdevKeySource? keys = null,
    // no key source because readKeyboardsForHotkeys is off, so the portal's shortcuts are not a fallback to explain
    bool keyboardsOff = false,
    // tests: starts the Hyprland layout watch for an attach, given the report callback; null starts the real one
    Func<Action<HyprlandLayoutWatch, uint>, HyprlandLayoutWatch?>? layoutWatches = null)
    : IPlatformInput, IBarrierHits
{
    // mutter's smooth scroll is clutter's steps times 10, a step a notch; other compositors may scale differently
    private const double EiDeltaPerNotch = 10;
    private static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(2);
    // a portal call that has not answered by then is given up on, so its lane moves on
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);
    // the first bind may wait on the desktop's dialog for the user
    private static readonly TimeSpan BindTimeout = TimeSpan.FromMinutes(2);
    // how long a barrier hit may wait for the router to enter or stay home before it is released
    private static readonly TimeSpan UnansweredHit = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryFirst = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryMax = TimeSpan.FromSeconds(60);
    // refused barrier hits are logged at most this often
    private static readonly TimeSpan RefusalLog = TimeSpan.FromSeconds(1);

    // the run's: the portal session, and the portal clipboard, which each visit claims while its capture is on; none
    // with clipboardSync Off
    public WaylandInputHandler(IWaylandSession session, IHydraProfile profile, ILogger<WaylandInputHandler> log, bool hyprland, ICaptureClipboard? clipboard = null,
        IEvdevKeySource? keys = null, bool keyboardsOff = false)
        : this(new WaylandCaptureSession(session), profile, clipboard, TimeProvider.System, log, hyprland: hyprland, keys: keys, keyboardsOff: keyboardsOff)
    {
    }

    private readonly Lock _lock = new();
    private readonly SerialWork _releases = new(log);
    private readonly SerialWork _work = new(log);
    // the handler's, not a portal session's: a session the portal replaces does not warn again
    private readonly UntriggeredWarning _untriggered = new();
    private CancellationTokenSource _stop = new();
    // _stop's token, kept so it can be read after _stop is disposed; cancelled until the first tap
    private CancellationToken _cancel = new(canceled: true);

    private Action<double, double>? _onMouseMove;
    private Action<double, double, long>? _onHit;
    private Action<double, double>? _onMouseDelta;
    private Action<KeyEvent>? _onKeyEvent;
    private Action<MouseButtonEvent>? _onMouseButton;
    private Action<MouseScrollEvent>? _onMouseScroll;

    private ICapturePortal? _portal;
    private ICaptureReceiver? _receiver;
    private bool _tapped;
    private bool _attaching;
    private int _attachFailures;
    // bumped on every SessionLost, so an attach that straddled one knows its session is gone
    private int _losses;

    private IReadOnlyList<CrossableEdge> _edges = [];
    private Dictionary<uint, BarrierLine> _barriers = [];
    private uint _lastBarrierId;
    private bool _barriersQueued;
    private int _barrierFailures;
    // an update that came while a capture was on, applied once it is released: setting barriers disables the session
    private bool _barriersDeferred;
    private int _disables;

    private Activation? _activation;
    // the capture under a hit still being decided was released for a drag's orphan up (Hyprland): the entry that follows
    // finds no capture for that reason, not for want of a barrier
    private bool _releasedForDrag;
    // the serial the next barrier hit takes
    private long _hits;
    private long? _refusalLoggedAt;
    private long _refusedSinceLog;
    private bool _onVirtual;
    private readonly HashSet<uint> _keysHeld = [];
    private readonly HashSet<uint> _buttonsHeld = [];
    private readonly MotionSum _motion = new();
    // releases queued and not yet through, which barrier work waits for
    private int _releasing;
    private double _scrollCarryX, _scrollCarryY;

    private EvdevKeyResolver? _resolver;
    // the keymap text _resolver was built from; null for the system layout
    private string? _keymap;
    // which key types a keysym on _resolver's own keymap, for the shortcuts' chords; null when it did not compile
    private XkbKeymapLookup? _lookup;
    // the keyboards' keys at home on _resolver's keymap, apart from it: a visit resets _resolver as it begins, which
    // would forget the down of a key pressed at home and released during the visit; null with no key source
    private EvdevKeyResolver? _homeResolver;
    // the compositor's effective layout group from the last EIS keyboard modifiers or Hyprland's IPC, applied to every
    // resolver; null until the first
    private uint? _layoutGroup;
    // Hyprland's active layout, whose portal sends no EIS keyboard modifiers; null elsewhere, and while detached
    private HyprlandLayoutWatch? _layoutWatch;
    // the capture's first key asks Hyprland's layout again: moving to another keyboard on another group raises no event
    private bool _refreshOnKey;

    private GlobalShortcutsPortal? _shortcuts;
    // the session last bound: a portal session binds once, and a re-tap on the same one keeps what it bound
    private GlobalShortcutsPortal? _boundOn;
    private int _bindFailures;
    private Dictionary<string, ShortcutChord> _chords = [];
    private readonly HeldChords _heldChords = new();
    // null until the first attach decides: true when the keyboards are read for the hotkeys at home, false for the portal's
    private bool? _readsKeyboards;
    private readonly Lock _readsKeyboardsLock = new();
    // keys the keyboards pressed at home, whose release goes home even during a visit
    private readonly HashSet<uint> _homeKeysHeld = [];
    private int _keysDisposed;

    public event Action? CaptureFailed;
    public event Action? KeyboardCapturedLate { add { } remove { } }

    public bool IsOnVirtualScreen
    {
        get
        {
            lock (_lock) return _onVirtual;
        }
        set
        {
            if (value) Enter();
            else EndCapture(null);
        }
    }

    public void ReturnTo(ScreenPoint? warpTo) => EndCapture(warpTo);

    public ValueTask HideCursor() => ValueTask.CompletedTask;   // the compositor hides it while capturing
    public ValueTask ShowCursor() => ValueTask.CompletedTask;   // and shows it on release
    public void WarpCursor(int x, int y) { }   // Wayland lets no client place the pointer; a return's warp rides on Release
    public ValueTask WarpToPark(int x, int y) => ValueTask.CompletedTask;   // the compositor holds the pointer still while capturing
    public bool RecentresItself => false;
    public bool SeesSwallowed(MouseButton button) => false;
    public bool KeyboardCaptured => true;

    // what the receiver saw held during the capture; at home the compositor tells us nothing
    public bool AnyMouseButtonHeld()
    {
        lock (_lock) return Captured && _buttonsHeld.Count > 0;
    }

    public bool AnyKeyHeld()
    {
        lock (_lock) return Captured && _keysHeld.Count > 0;
    }

    // for tests: completes once the barrier work queued so far has run
    internal Task BarrierWorkIdle => _work.Idle;

    // under _lock: a visit, or a hit the router is still deciding on
    private bool Captured => _onVirtual || _activation != null;

    public Task StartEventTap(
        Action<double, double> onMouseMove,
        Action<double, double>? onMouseDelta,
        Action<KeyEvent> onKeyEvent,
        Action<MouseButtonEvent> onMouseButton,
        Action<MouseScrollEvent> onMouseScroll,
        Action? onLocalActivity = null)
    {
        StopEventTap();
        lock (_lock)
        {
            _onMouseMove = onMouseMove;
            _onMouseDelta = onMouseDelta;
            _onKeyEvent = onKeyEvent;
            _onMouseButton = onMouseButton;
            _onMouseScroll = onMouseScroll;
            // the old one is cancelled; work still holding its token only reads it
            _stop.Dispose();
            _stop = new CancellationTokenSource();
            _cancel = _stop.Token;
            _tapped = true;
            _attachFailures = 0;
        }
        session.ScreensChanged += OnScreensChanged;
        session.SessionLost += OnSessionLost;
        EnsureAttached();
        return Task.CompletedTask;
    }

    public void StopEventTap()
    {
        ICapturePortal? portal;
        IReadOnlyList<uint> ups;
        lock (_lock)
        {
            if (!_tapped) return;
            _tapped = false;
            portal = _portal;
            ups = _heldChords.ReleaseAll();
            DetachLocked();
            _stop.Cancel();
        }
        PressChordKeys(ups, down: false);
        session.ScreensChanged -= OnScreensChanged;
        session.SessionLost -= OnSessionLost;
        // whatever we think its state: another compositor than mutter may still have it enabled
        if (portal != null) _ = _releases.Enqueue("Disabling input capture", _ => portal.Disable(), CallTimeout, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        StopEventTap();
        try
        {
            await Task.WhenAll(_releases.Idle, _work.Idle).WaitAsync(DisposeWait);
        }
        catch (TimeoutException)
        {
            if (log.IsEnabled(LogLevel.Warning)) log.LogWarning("Input capture calls still running at shutdown");
        }
        lock (_lock)
        {
            _resolver?.Dispose();
            _resolver = null;
            _homeResolver?.Dispose();
            _homeResolver = null;
            _lookup?.Dispose();
            _lookup = null;
            _keymap = null;
            _stop.Dispose();
        }
        // under _readsKeyboardsLock, so an attach deciding now either started the source before this or sees it disposed
        lock (_readsKeyboardsLock)
        {
            if (keys is not { } source || Interlocked.Exchange(ref _keysDisposed, 1) != 0) return;
            source.Key -= OnHomeKey;
            source.Dispose();
        }
    }

    public void CrossableEdgesChanged(IReadOnlyList<CrossableEdge> edges)
    {
        lock (_lock)
        {
            // every layout rebuild reports them; resetting barriers that did not change ends a hit with our own Disable
            if (_edges.Count == edges.Count && _edges.ToHashSet().SetEquals(edges)) return;
            _edges = edges;
        }
        RequestBarriers();
    }

    // A barrier hit the router did not take (a dead corner, a stretch of the edge with no link): the pointer goes back
    // just inside the edge it hit, with no wait but the Release itself. Pushing on into that corner hits again once the
    // release is through, so hits and releases alternate one at a time and nothing queues up; the log keeps to one line
    // a second for them. Only the hit with that serial is released: an answer coming after a newer hit leaves that one be
    public void StayedHome(long hit)
    {
        lock (_lock)
        {
            // the latest hit, its capture released for a drag's orphan up, refused: no entry follows to explain that to
            if (hit == _hits && !_onVirtual && _activation is null) _releasedForDrag = false;
        }
        if (ReleaseIfUndecided(a => a.Serial == hit) is not { } refused || !log.IsEnabled(LogLevel.Debug)) return;
        var now = time.GetTimestamp();
        long suppressed;
        lock (_lock)
        {
            _refusedSinceLog++;
            if (_refusalLoggedAt is { } last && time.GetElapsedTime(last, now) < RefusalLog) return;
            _refusalLoggedAt = now;
            suppressed = _refusedSinceLog - 1;
            _refusedSinceLog = 0;
        }
        log.LogDebug("Barrier hit on the {Barrier} at {Hit} starts no crossing; released ({Suppressed} more since the last line)",
            refused.Barrier?.ToString() ?? "unknown barrier", refused.Hit, suppressed);
    }

    public void ReportHitsTo(Action<double, double, long> onHit)
    {
        lock (_lock) _onHit = onHit;
    }

    // releases the current activation if it is the one asked about and the router has not entered a visit on it; the
    // one released, or null
    private Activation? ReleaseIfUndecided(Func<Activation, bool> only)
    {
        Activation activation;
        ReleaseClaim? claim;
        lock (_lock)
        {
            if (_onVirtual || _activation is null || !only(_activation)) return null;
            activation = _activation;
            EndKeysLocked();
            _activation = null;
            claim = ClaimReleaseLocked();
        }
        Release(claim, activation, activation.Barrier is { } barrier ? WaylandBarriers.ReleasePoint(barrier, activation.Hit) : null, quiet: true);
        return activation;
    }

    // -- session --

    // one attach at a time, retried with backoff until it holds; also how a session started by someone else is hooked
    private void EnsureAttached()
    {
        CancellationToken cancel;
        lock (_lock)
        {
            if (!_tapped || _attaching || _portal != null) return;
            _attaching = true;
            cancel = _cancel;
        }
        _ = Task.Run(() => AttachLoop(cancel), CancellationToken.None);
    }

    private async Task AttachLoop(CancellationToken cancel)
    {
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                try
                {
                    await Attach(cancel);
                    return;
                }
                catch (Exception e) when (!cancel.IsCancellationRequested)
                {
                    int failures;
                    lock (_lock) failures = _attachFailures++;
                    var wait = WaylandBarriers.Backoff(failures, RetryFirst, RetryMax);
                    if (log.IsEnabled(LogLevel.Warning)) log.LogWarning("Attaching to the input capture session failed ({Error}); trying again in {Wait}", e.Message, wait);
                    await Task.Delay(wait, time, cancel);
                }
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_lock) _attaching = false;
        }
    }

    private async Task Attach(CancellationToken cancel)
    {
        int losses;
        lock (_lock) losses = _losses;
        var portal = await session.InputCapture(cancel);
        var receiver = await session.Receiver(cancel);
        lock (_lock)
        {
            if (!_tapped || cancel.IsCancellationRequested) return;
            // the session these came from was lost while they were fetched: its SessionLost found nothing to detach
            if (_losses != losses) throw new PortalException("The portal session was lost while attaching to it");
            DetachLocked();
            _portal = portal;
            _receiver = receiver;
            _attachFailures = 0;
            portal.Activated += OnActivated;
            portal.Deactivated += OnDeactivated;
            portal.Disabled += OnDisabled;
            portal.ZonesChanged += OnZonesChanged;
            receiver.KeyboardKeymapChanged += UseKeymap;
            receiver.PointerMotion += OnPointerMotion;
            receiver.Frame += OnFrame;
            receiver.Button += OnButton;
            receiver.ScrollDiscrete += OnScrollDiscrete;
            receiver.ScrollDelta += OnScrollDelta;
            receiver.ScrollStop += OnScrollStop;
            receiver.KeyboardKey += OnKeyboardKey;
            receiver.KeyboardModifiers += OnKeyboardModifiers;
            if (hyprland) _layoutWatch = layoutWatches is null ? HyprlandLayoutWatch.Start(FollowWatchedLayout, log, time) : layoutWatches(FollowWatchedLayout);
        }
        // a connection that dropped before the handlers were attached is a lost session, to be attached afresh
        if (receiver.IsDisconnected)
        {
            lock (_lock) DetachLocked();
            throw new PortalException("The EIS connection dropped while attaching to it");
        }
        // read after subscribing, so a keymap announced in between is not missed
        UseKeymap(receiver.KeyboardKeymap);
        // decided before the barriers, so no capture starts while the hotkeys at home are undecided
        if (!ReadsKeyboards()) BindShortcutsSoon(TimeSpan.Zero);
        RequestBarriers();
    }

    // once a run: the keyboards when the key source opens any, else the portal's shortcuts. The whole decision runs
    // under _readsKeyboardsLock, its own lock and not _lock, since Start opens the input devices and may join a thread:
    // racing attaches decide once and the later waits for the earlier's outcome, without stalling input under _lock
    private bool ReadsKeyboards()
    {
        lock (_readsKeyboardsLock)
        {
            lock (_lock)
            {
                if (_readsKeyboards is { } decided) return decided;
            }
            // a source disposed already is never started: nothing would stop its fds or its watch thread
            if (Volatile.Read(ref _keysDisposed) != 0) return false;
            var reads = false;
            if (keys is { } source)
            {
                source.Key += OnHomeKey;
                reads = source.Start();
                if (!reads) source.Key -= OnHomeKey;
            }
            lock (_lock) _readsKeyboards = reads;
            if (reads)
            {
                if (log.IsEnabled(LogLevel.Information)) log.LogInformation("Hotkeys at home: read from the keyboards (evdev)");
            }
            else if (keys != null && log.IsEnabled(LogLevel.Information))
            {
                log.LogInformation("Hotkeys at home: cannot read /dev/input, so the portal's shortcuts serve them; adding the user to the input group lets Hydra read every hotkey at home on Hyprland");
            }
            else if (keyboardsOff && log.IsEnabled(LogLevel.Information))
            {
                log.LogInformation("Hotkeys at home: the desktop's shortcut portal serves them, since readKeyboardsForHotkeys is off");
            }
            return reads;
        }
    }

    private void DetachLocked()
    {
        if (_portal is { } portal)
        {
            portal.Activated -= OnActivated;
            portal.Deactivated -= OnDeactivated;
            portal.Disabled -= OnDisabled;
            portal.ZonesChanged -= OnZonesChanged;
        }
        if (_receiver is { } receiver)
        {
            receiver.KeyboardKeymapChanged -= UseKeymap;
            receiver.PointerMotion -= OnPointerMotion;
            receiver.Frame -= OnFrame;
            receiver.Button -= OnButton;
            receiver.ScrollDiscrete -= OnScrollDiscrete;
            receiver.ScrollDelta -= OnScrollDelta;
            receiver.ScrollStop -= OnScrollStop;
            receiver.KeyboardKey -= OnKeyboardKey;
            receiver.KeyboardModifiers -= OnKeyboardModifiers;
        }
        _layoutWatch?.Dispose();
        _layoutWatch = null;
        DetachShortcutsLocked();
        _portal = null;
        _receiver = null;
        _barriers = [];
        _barriersQueued = false;
        _barriersDeferred = false;
        _activation = null;
    }

    // the session is gone with its barriers and any capture: a visit ends, and a fresh session takes over
    private void OnSessionLost()
    {
        bool captured;
        IReadOnlyList<uint> ups;
        lock (_lock)
        {
            _losses++;
            if (!_tapped) return;
            captured = _onVirtual && _activation != null;
            ups = _heldChords.ReleaseAll();
            EndKeysLocked();
            DetachLocked();
        }
        PressChordKeys(ups, down: false);
        if (log.IsEnabled(LogLevel.Warning)) log.LogWarning("Input capture session lost; starting a new one");
        if (captured) CaptureFailed?.Invoke();
        EnsureAttached();
    }

    // a fresh session (the screen detector may have started it) is hooked here too
    private void OnScreensChanged()
    {
        EnsureAttached();
        RequestBarriers();
    }

    // the zones moved (mutter disables the session as they do): barriers anew
    private void OnZonesChanged() => RequestBarriers();

    // Deskflow's answer too: nothing says why, so try again, backing off while it keeps happening
    private void OnDisabled()
    {
        bool captured;
        CancellationToken cancel;
        TimeSpan wait;
        lock (_lock)
        {
            captured = _onVirtual && _activation != null;
            EndKeysLocked();
            _activation = null;
            cancel = _cancel;
            wait = WaylandBarriers.Backoff(_disables++, RetryFirst, RetryMax);
        }
        if (log.IsEnabled(LogLevel.Information)) log.LogInformation("Input capture disabled by the compositor; enabling it again in {Wait}", wait);
        if (captured) CaptureFailed?.Invoke();
        After(wait, RequestBarriers, cancel);
    }

    private void After(TimeSpan wait, Action then, CancellationToken cancel) =>
        _ = Task.Delay(wait, time, cancel).ContinueWith(t =>
        {
            if (!t.IsCanceled) then();
        }, TaskScheduler.Default);

    // -- barriers --

    // one update queued at most: the one that runs reads the newest edges
    private void RequestBarriers()
    {
        CancellationToken cancel;
        lock (_lock)
        {
            if (!_tapped || _portal is null || _barriersQueued) return;
            _barriersQueued = true;
            cancel = _cancel;
        }
        _ = ApplyBarriersThenRetry(cancel);
    }

    private async Task ApplyBarriersThenRetry(CancellationToken cancel)
    {
        if (await _work.Enqueue("Setting pointer barriers", ApplyBarriers, CallTimeout, cancel)) return;
        TimeSpan wait;
        lock (_lock)
        {
            if (!_tapped || _portal is null || cancel.IsCancellationRequested) return;
            wait = WaylandBarriers.Backoff(_barrierFailures++, RetryFirst, RetryMax);
        }
        if (log.IsEnabled(LogLevel.Information)) log.LogInformation("Setting the pointer barriers again in {Wait}", wait);
        After(wait, RequestBarriers, cancel);
    }

    private async Task ApplyBarriers(CancellationToken cancel)
    {
        ICapturePortal? portal;
        IReadOnlyList<CrossableEdge> edges;
        lock (_lock)
        {
            _barriersQueued = false;
            portal = _portal;
            if (portal is null || DeferLocked()) return;
            edges = _edges;
        }

        var zones = await session.Zones(cancel);
        var lines = WaylandBarriers.Lines(edges, WaylandBarriers.Bounds(zones));

        // barriers are set on a disabled session only, so it is always disabled first, whatever we last saw (mutter
        // disables itself as its monitors change, others need not). A hit that came while the zones were read keeps the
        // session as it is; one racing the disable itself is ended by it
        lock (_lock)
        {
            if (_portal != portal || DeferLocked()) return;
        }
        await DisableForBarriers(portal);

        var ids = WaylandBarriers.Ids(ref _lastBarrierId, lines.Count);
        var barriers = new List<PortalBarrier>(lines.Count);
        var byId = new Dictionary<uint, BarrierLine>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            barriers.Add(new PortalBarrier(ids[i], lines[i].X1, lines[i].Y1, lines[i].X2, lines[i].Y2));
            byId[ids[i]] = lines[i];
        }
        var failed = await portal.SetPointerBarriers(barriers, zones.Serial, cancel);
        foreach (var id in failed)
        {
            if (byId.Remove(id, out var line) && log.IsEnabled(LogLevel.Warning))
                log.LogWarning("The compositor refused the pointer barrier on the {Barrier}", line);
        }
        if (log.IsEnabled(LogLevel.Debug))
            log.LogDebug("Pointer barriers set: {Barriers}", byId.Count == 0 ? "none" : string.Join(", ", byId.Values));

        lock (_lock)
        {
            if (_portal != portal) return;
            _barriers = byId;
            _barrierFailures = 0;
        }
        if (byId.Count == 0) return;
        await portal.Enable();
    }

    // under _lock: a capture is on, or a release is queued (a return's held one among them), so the update waits for it:
    // its Disable would end the capture, and the Release on the disabled session would fail
    private bool DeferLocked()
    {
        _barriersDeferred = _activation != null || _releasing > 0;
        return _barriersDeferred;
    }

    // our own Disable ends any capture it raced, with no Deactivated to say so
    private async Task DisableForBarriers(ICapturePortal portal)
    {
        try
        {
            await portal.Disable();
        }
        catch (DBusErrorReplyException e)
        {
            // not enabled, which is what we want
            if (log.IsEnabled(LogLevel.Debug)) log.LogDebug("Disabling input capture before new barriers: {Error}", e.Message);
        }
        bool captured;
        lock (_lock)
        {
            if (_activation is null) return;
            captured = _onVirtual;
            EndKeysLocked();
            _activation = null;
        }
        if (captured) CaptureFailed?.Invoke();
    }

    // -- capture --

    private void OnActivated(uint activationId, double x, double y, uint barrierId)
    {
        Action<double, double>? onMouseMove;
        Action<double, double, long>? onHit;
        Activation activation;
        CancellationToken cancel;
        ReleaseClaim? claim = null;
        HyprlandLayoutWatch? layoutWatch;
        lock (_lock)
        {
            var barrier = _barriers.GetValueOrDefault(barrierId) ?? WaylandBarriers.Nearest(_barriers.Values, x, y);
            var hit = barrier is null ? new ScreenPoint((int)x, (int)y) : WaylandBarriers.HitPoint(barrier, x, y);
            activation = new Activation(activationId, barrier, hit, ++_hits);
            _activation = barrier is null ? null : activation;
            _releasedForDrag = false;
            _disables = 0;
            ForgetHeldLocked();
            onMouseMove = _onMouseMove;
            onHit = _onHit;
            cancel = _cancel;
            if (barrier is null) claim = ClaimReleaseLocked();
            _refreshOnKey = barrier != null;
            layoutWatch = _layoutWatch;
        }
        if (activation.Barrier is null)
        {
            Release(claim, activation, null);
            return;
        }
        // the keyboard last typed on may be another one, on another group, than when Hyprland last said
        _ = layoutWatch?.Refresh(after: time.GetTimestamp());
        // the router answers every absolute move, but one it drops (no layout yet, a failure) would hold the capture
        After(UnansweredHit, () => ReleaseUnanswered(activation), cancel);
        if (onHit != null) onHit(activation.Hit.X, activation.Hit.Y, activation.Serial);
        else onMouseMove?.Invoke(activation.Hit.X, activation.Hit.Y);
    }

    private void ReleaseUnanswered(Activation activation)
    {
        lock (_lock)
        {
            if (_onVirtual || !ReferenceEquals(_activation, activation)) return;
        }
        if (log.IsEnabled(LogLevel.Warning)) log.LogWarning("Input capture {Id}: the router did not answer the barrier hit; releasing it", activation.Id);
        ReleaseIfUndecided(a => ReferenceEquals(a, activation));
    }

    // the compositor ended a capture (its own escape, or a release we raced); a visit on it ends with it
    private void OnDeactivated(uint activationId)
    {
        bool captured;
        lock (_lock)
        {
            if (_activation?.Id != activationId) return;
            captured = _onVirtual;
            EndKeysLocked();
            _activation = null;
        }
        if (log.IsEnabled(LogLevel.Debug)) log.LogDebug("Input capture {Id} deactivated", activationId);
        if (captured) CaptureFailed?.Invoke();
        RequestDeferredBarriers();
    }

    // an entry with no capture under it (a monitor switch, a remote-only entry) would leave the input on the desktop
    private void Enter()
    {
        bool uncaptured;
        bool releasedForDrag;
        EvdevKeyResolver? resolver;
        lock (_lock)
        {
            if (_onVirtual) return;
            _onVirtual = true;
            uncaptured = _activation is null;
            releasedForDrag = _releasedForDrag;
            _releasedForDrag = false;
            // the visit's motion starts here: none from before the entry, nor a frame half summed across it
            _motion.Reset();
            _scrollCarryX = _scrollCarryY = 0;
            resolver = _resolver;
        }
        resolver?.Reset();
        // the reset's fresh state starts on the first group; the compositor's stays in effect
        lock (_lock) ApplyLayoutLocked(resolver);
        if (!uncaptured)
        {
            // the portal sets the clipboard only while the capture is on, so the visit claims it now (off the router's
            // actor: it reads the clipboard), and the return releases at once
            if (clipboard is { } capturing) _ = Task.Run(capturing.Claim, CancellationToken.None);
            return;
        }
        if (releasedForDrag)
        {
            if (log.IsEnabled(LogLevel.Debug)) log.LogDebug("A visit began after its capture was released for a drag across the edge");
        }
        else if (log.IsEnabled(LogLevel.Warning))
            log.LogWarning("A visit began with no input capture; Wayland only captures once the pointer hits a barrier");
        CaptureFailed?.Invoke();
    }

    // a return, a refused hit or an unanswered one: the capture is released at warpTo, else just inside the edge it hit
    private void EndCapture(ScreenPoint? warpTo)
    {
        Activation? activation;
        ReleaseClaim? claim;
        lock (_lock)
        {
            EndKeysLocked();
            _onVirtual = false;
            // a return: no entry follows to explain a drag's release to (a refused hit clears this in StayedHome)
            _releasedForDrag = false;
            activation = _activation;
            _activation = null;
            if (activation is null) return;
            claim = ClaimReleaseLocked();
        }
        Release(claim, activation, warpTo ?? (activation.Barrier is { } barrier ? WaylandBarriers.ReleasePoint(barrier, activation.Hit) : null));
    }

    // under _lock, in the section that ends the capture, so no barrier work slips in between: the release counted as
    // queued, which defers barrier work until it is through (DeferLocked). Null with no portal
    private ReleaseClaim? ClaimReleaseLocked()
    {
        if (_portal is not { } portal) return null;
        _releasing++;
        return new ReleaseClaim(portal, _cancel);
    }

    // quiet: a refused hit, which StayedHome logs at its own rate. Barrier work runs once the release is through: a
    // Disable before it would end the capture, and the Release on it would then fail
    private void Release(ReleaseClaim? claim, Activation activation, ScreenPoint? at, bool quiet = false)
    {
        if (claim is null)
        {
            RequestDeferredBarriers();
            return;
        }
        if (!quiet && log.IsEnabled(LogLevel.Debug)) log.LogDebug("Releasing input capture {Id} at {At}", activation.Id, at?.ToString() ?? "the hit");
        _ = ReleaseThenBarriers(claim, activation, at, quiet);
    }

    private async Task ReleaseThenBarriers(ReleaseClaim claim, Activation activation, ScreenPoint? at, bool quiet)
    {
        try
        {
            await _releases.Enqueue("Releasing input capture", async _ =>
            {
                await claim.Portal.Release(activation.Id, at?.X, at?.Y);
                if (!quiet && log.IsEnabled(LogLevel.Information)) log.LogInformation("Input capture {Id} released", activation.Id);
            }, CallTimeout, claim.Cancel);
        }
        finally
        {
            lock (_lock) _releasing--;
            RequestDeferredBarriers();
        }
    }

    private void RequestDeferredBarriers()
    {
        lock (_lock)
        {
            if (!_barriersDeferred) return;
        }
        RequestBarriers();
    }

    private void ForgetHeldLocked()
    {
        _keysHeld.Clear();
        _buttonsHeld.Clear();
    }

    // a capture ends: no ups for what it held will reach EIS, so the resolver's modifiers would stay down at home, where
    // a shortcut's chord then carries them and matches no hotkey. Called before the capture's state is cleared
    private void EndKeysLocked()
    {
        if (Captured)
        {
            _resolver?.Reset();
            ApplyLayoutLocked(_resolver);
        }
        ForgetHeldLocked();
    }

    // under _lock: the compositor's layout group, once it has told us one. pin, for the home resolver only: the compositor
    // sees the keys at home too, so a toggle there comes back in its next report, and moving on it as well would apply it
    // twice. The capture's resolver also toggles on its own keys, whatever the compositor makes of them, until a report
    // says otherwise
    private void ApplyLayoutLocked(EvdevKeyResolver? resolver, bool pin = false)
    {
        if (_layoutGroup is { } group) resolver?.SetLayout(group, pin);
    }

    // -- receiver, on the EIS dispatch thread --

    // the compositor's keymap (any keyboard's, paused or not), else the layout the system names
    private void UseKeymap(string? keymap)
    {
        if (!useKeymaps) return;
        lock (_lock)
        {
            if (keymap is null ? _resolver != null : keymap == _keymap) return;
        }
        var resolver = keymap is null ? EvdevKeyResolver.FromSystemLayout(log) : KeymapResolver(keymap);
        if (resolver is null) return;
        var lookup = Lookup(resolver);
        var home = HomeResolver(resolver);
        EvdevKeyResolver? old, oldHome;
        XkbKeymapLookup? oldLookup;
        lock (_lock)
        {
            old = _resolver;
            oldHome = _homeResolver;
            oldLookup = _lookup;
            _resolver = resolver;
            _homeResolver = home;
            _lookup = lookup;
            _keymap = keymap;
            // a keymap can come after the modifiers that named the group
            ApplyLayoutLocked(resolver);
            ApplyLayoutLocked(home, pin: true);
        }
        old?.Dispose();
        oldHome?.Dispose();
        oldLookup?.Dispose();
    }

    private EvdevKeyResolver? HomeResolver(EvdevKeyResolver resolver)
    {
        if (keys is null) return null;
        try
        {
            return EvdevKeyResolver.FromKeymap(resolver.KeymapText());
        }
        catch (InvalidOperationException e)
        {
            if (log.IsEnabled(LogLevel.Warning)) log.LogWarning("The keymap for the keyboards' hotkeys at home did not compile: {Error}", e.Message);
            return null;
        }
    }

    // over the resolver's own keymap, so a chord's keys are the ones the resolver reads back
    private XkbKeymapLookup? Lookup(EvdevKeyResolver resolver)
    {
        try
        {
            return new XkbKeymapLookup(resolver.KeymapText());
        }
        catch (InvalidOperationException e)
        {
            if (log.IsEnabled(LogLevel.Warning)) log.LogWarning("The keymap for the hotkeys at home did not compile: {Error}", e.Message);
            return null;
        }
    }

    private EvdevKeyResolver? KeymapResolver(string keymap)
    {
        try
        {
            var resolver = EvdevKeyResolver.FromKeymap(keymap);
            if (log.IsEnabled(LogLevel.Information)) log.LogInformation("Keyboard layout: the compositor's keymap");
            return resolver;
        }
        catch (InvalidOperationException e)
        {
            if (log.IsEnabled(LogLevel.Warning)) log.LogWarning(e, "The compositor's keymap did not compile; keeping the layout in use");
            return null;
        }
    }

    // motion sums per frame, as evdev's per SYN_REPORT, and only once the visit is entered: motion between a barrier hit
    // and the router's entry belongs to no visit, and fed in as the visit's first deltas it would carry a cursor that
    // entered beside the return edge straight back home
    private void OnPointerMotion(double dx, double dy)
    {
        lock (_lock)
        {
            _motion.Add(dx, dy, _onVirtual);
        }
    }

    private void OnFrame()
    {
        Action<double, double>? onMouseDelta;
        FrameMotion motion;
        lock (_lock)
        {
            if (_motion.Take(_onVirtual) is not { } taken) return;
            motion = taken;
            onMouseDelta = _onMouseDelta;
        }
        onMouseDelta?.Invoke(motion.Dx, motion.Dy);
    }

    // A button held as the capture began (a drag into the barrier) went down on the desktop, which nothing here can ask
    // about, so the visit cannot be refused for it. Its up then arrives over EIS with no down: it is dropped, never sent
    // to the slave as an up it had no down for. Only a button pressed during this capture is forwarded both ways. On
    // Hyprland that up ends the capture instead: xdph sends no more input after it, which would freeze the user's input
    private void OnButton(uint code, bool pressed)
    {
        Action<MouseButtonEvent>? onMouseButton;
        Activation? orphaned;
        ReleaseClaim? claim = null;
        var visiting = false;
        lock (_lock)
        {
            var tracked = TrackButton(_buttonsHeld, code, pressed);
            if (tracked ? !_onVirtual : !hyprland || _activation is null) return;
            onMouseButton = _onMouseButton;
            orphaned = tracked ? null : _activation;
            if (orphaned != null)
            {
                visiting = _onVirtual;
                // a hit the router is still deciding on: its entry finds no capture, for a reason Enter names
                _releasedForDrag = !visiting;
                EndKeysLocked();
                _onVirtual = false;
                _activation = null;
                claim = ClaimReleaseLocked();
            }
        }
        if (orphaned is null)
        {
            onMouseButton?.Invoke(new MouseButtonEvent(EvdevButtons.Button(code), pressed));
            return;
        }
        if (log.IsEnabled(LogLevel.Information))
            log.LogInformation("A button held across the edge ends the visit as it is released: Hyprland's portal carries no drag across it");
        Release(claim, orphaned, orphaned.Barrier is { } barrier ? WaylandBarriers.ReleasePoint(barrier, orphaned.Hit) : null);
        // an entry the router has not made yet finds no capture and fails on its own
        if (visiting) CaptureFailed?.Invoke();
    }

    private void OnScrollDiscrete(int dx, int dy)
    {
        Action<MouseScrollEvent>? onMouseScroll;
        lock (_lock)
        {
            if (!_onVirtual) return;
            onMouseScroll = _onMouseScroll;
        }
        onMouseScroll?.Invoke(DiscreteScroll(dx, dy));
    }

    private void OnScrollDelta(double dx, double dy)
    {
        MouseScrollEvent scroll;
        Action<MouseScrollEvent>? onMouseScroll;
        lock (_lock)
        {
            if (!_onVirtual) return;
            scroll = new MouseScrollEvent(SmoothScroll(dx, ref _scrollCarryX), SmoothScroll(-dy, ref _scrollCarryY));
            onMouseScroll = _onMouseScroll;
        }
        if (scroll != default) onMouseScroll?.Invoke(scroll);
    }

    private void OnScrollStop(bool x, bool y)
    {
        lock (_lock)
        {
            if (x) _scrollCarryX = 0;
            if (y) _scrollCarryY = 0;
        }
    }

    // the compositor's modifiers: only the layout group is taken, our own modifiers coming from the keys. The resolvers
    // follow it, as a layout switch on the desktop types the new layout's characters on the slave
    private void OnKeyboardModifiers(uint depressed, uint latched, uint locked, uint group) => FollowLayout(group);

    // the compositor's layout group, from EIS or Hyprland's IPC, whichever told us last
    private void FollowLayout(uint group)
    {
        bool changed;
        lock (_lock) changed = FollowLayoutLocked(group);
        LogLayout(changed, group);
    }

    // a report from a watch already detached (its query straddled the detach) is dropped, and so is one naming the group
    // in use: applying it would undo a toggle made inside the capture since
    private void FollowWatchedLayout(HyprlandLayoutWatch watch, uint group)
    {
        bool changed;
        lock (_lock)
        {
            if (!ReferenceEquals(_layoutWatch, watch) || _layoutGroup == group) return;
            changed = FollowLayoutLocked(group);
        }
        LogLayout(changed, group);
    }

    private bool FollowLayoutLocked(uint group)
    {
        var changed = _layoutGroup != group;
        _layoutGroup = group;
        ApplyLayoutLocked(_resolver);
        ApplyLayoutLocked(_homeResolver, pin: true);
        return changed;
    }

    private void LogLayout(bool changed, uint group)
    {
        if (changed && log.IsEnabled(LogLevel.Debug)) log.LogDebug("Compositor keyboard layout group: {Group}", group);
    }

    private void OnKeyboardKey(uint code, bool pressed)
    {
        var at = time.GetTimestamp();
        EvdevKeyResolver? resolver;
        Action<KeyEvent>? onKeyEvent;
        bool forward;
        HyprlandLayoutWatch? layoutWatch = null;
        lock (_lock)
        {
            if (pressed) _keysHeld.Add(code);
            else _keysHeld.Remove(code);
            resolver = _resolver;
            forward = _onVirtual;
            onKeyEvent = _onKeyEvent;
            if (_refreshOnKey && Captured)
            {
                _refreshOnKey = false;
                layoutWatch = _layoutWatch;
            }
        }
        // asked again unless the capture's query asked after this key: that one may predate a switch to this keyboard.
        // the answer is async, so this very key, and any before it lands, still resolves under the old group; accepted
        _ = layoutWatch?.Refresh(after: at);
        // resolved even while the router decides, so the resolver's modifiers keep up; the resolver stamps PhysicalKey
        var events = resolver?.Resolve(code, pressed ? 1 : 0);
        if (!forward || events is null) return;
        foreach (var keyEvent in events)
            if (keyEvent is not null) onKeyEvent?.Invoke(keyEvent);
    }

    // -- global shortcuts, the hotkeys at home --

    private void BindShortcutsSoon(TimeSpan wait)
    {
        CancellationToken cancel;
        lock (_lock)
        {
            if (!_tapped) return;
            cancel = _cancel;
        }
        // its own lane: the first bind may wait on the desktop's dialog, and the barriers must not wait with it
        After(wait, () => _ = BindShortcutsThenRetry(cancel), cancel);
    }

    private async Task BindShortcutsThenRetry(CancellationToken cancel)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            deadline.CancelAfter(BindTimeout);
            await BindShortcuts(deadline.Token);
            lock (_lock) _bindFailures = 0;
        }
        catch (Exception e) when (!cancel.IsCancellationRequested)
        {
            TimeSpan wait;
            lock (_lock)
            {
                // a bind that got nowhere may be tried on the same session again
                _boundOn = null;
                wait = WaylandBarriers.Backoff(_bindFailures++, RetryFirst, RetryMax);
            }
            if (log.IsEnabled(LogLevel.Warning)) log.LogWarning("Binding the global shortcuts failed ({Error}); trying again in {Wait}", e.Message, wait);
            BindShortcutsSoon(wait);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task BindShortcuts(CancellationToken cancel)
    {
        var shortcuts = await session.Shortcuts(cancel);
        if (shortcuts is null) return;
        var chords = WaylandShortcuts.Chords(profile.Hotkeys);
        if (chords.Count == 0) return;
        bool bind;
        lock (_lock)
        {
            if (!_tapped || cancel.IsCancellationRequested) return;
            if (_shortcuts != shortcuts)
            {
                DetachShortcutsLocked();
                shortcuts.Activated += OnShortcutActivated;
                shortcuts.Deactivated += OnShortcutDeactivated;
                shortcuts.ShortcutsChanged += OnShortcutsChanged;
                shortcuts.Closed += OnShortcutsClosed;
                _shortcuts = shortcuts;
            }
            _chords = chords.ToDictionary(c => c.Id);
            bind = _boundOn != shortcuts;
            _boundOn = shortcuts;
        }
        // a session closed before its Closed handler was attached is caught by WaylandSession, which forgets it
        if (!bind) return;

        var requests = chords.Select(WaylandShortcuts.Request).ToList();
        var bound = await shortcuts.BindShortcuts(requests, _untriggered, cancel);
        if (log.IsEnabled(LogLevel.Information))
            log.LogInformation("Hotkeys at home: {Known} of {Asked} with a trigger the portal knows", bound.Count, requests.Count);
        if (log.IsEnabled(LogLevel.Debug))
        {
            foreach (var request in requests)
                log.LogDebug("Global shortcut {Id}: asked for {Trigger}, {Bound}", request.Id, request.Trigger ?? "the user's choice",
                    bound.FirstOrDefault(s => s.Id == request.Id) is { } match ? $"bound to {match.TriggerDescription ?? "a trigger"}" : "not bound");
        }
    }

    private void DetachShortcutsLocked()
    {
        if (_shortcuts is not { } shortcuts) return;
        shortcuts.Activated -= OnShortcutActivated;
        shortcuts.Deactivated -= OnShortcutDeactivated;
        shortcuts.ShortcutsChanged -= OnShortcutsChanged;
        shortcuts.Closed -= OnShortcutsClosed;
        _shortcuts = null;
    }

    // the portal closed the shortcuts session: what it held is let go, and a fresh one is bound
    private void OnShortcutsClosed()
    {
        IReadOnlyList<uint> ups;
        TimeSpan wait;
        lock (_lock)
        {
            if (!_tapped) return;
            DetachShortcutsLocked();
            _boundOn = null;
            ups = _heldChords.ReleaseAll();
            wait = WaylandBarriers.Backoff(_bindFailures++, RetryFirst, RetryMax);
        }
        PressChordKeys(ups, down: false);
        if (log.IsEnabled(LogLevel.Warning)) log.LogWarning("The global shortcuts session closed; binding the hotkeys again in {Wait}", wait);
        BindShortcutsSoon(wait);
    }

    private void OnShortcutsChanged(IReadOnlyList<BoundShortcut> shortcuts)
    {
        if (log.IsEnabled(LogLevel.Information))
            log.LogInformation("Global shortcuts changed: {Shortcuts}", string.Join(", ", shortcuts.Select(s => $"{s.Id} = {s.TriggerDescription ?? "none"}")));
    }

    // a hotkey pressed at home: the router gets its chord's keys as if typed. During a capture its keys come through EIS
    private void OnShortcutActivated(string id, ulong timestamp)
    {
        ShortcutChord? chord;
        XkbKeymapLookup? lookup;
        lock (_lock)
        {
            if (!_tapped || Captured || !_chords.TryGetValue(id, out chord)) return;
            lookup = _lookup;
        }
        if (lookup is null || WaylandShortcuts.Keys(lookup, chord.Binding.Modifiers, lookup.EvdevKeyFor(WaylandShortcuts.Keysym(chord.Binding))) is not { } codes)
        {
            if (log.IsEnabled(LogLevel.Warning)) log.LogWarning("Global shortcut {Id} fired, but the keymap has no key for its chord", id);
            return;
        }
        IReadOnlyList<uint> ups, downs;
        lock (_lock)
        {
            if (Captured) return;
            // held already: its deactivation was lost, so it is let go before being pressed again
            ups = _heldChords.Release(id);
            downs = _heldChords.Press(id, codes);
        }
        if (log.IsEnabled(LogLevel.Debug)) log.LogDebug("Global shortcut {Id} activated", id);
        PressChordKeys(ups, down: false);
        PressChordKeys(downs, down: true);
    }

    // the ups of what the activation pressed, whatever happened since: a key pressed at home is released at home
    private void OnShortcutDeactivated(string id, ulong timestamp)
    {
        IReadOnlyList<uint> ups;
        lock (_lock) ups = _heldChords.Release(id);
        if (ups.Count > 0 && log.IsEnabled(LogLevel.Debug)) log.LogDebug("Global shortcut {Id} deactivated", id);
        PressChordKeys(ups, down: false);
    }

    // the router gets a chord's keys as if typed; internal for tests, which have no shortcuts portal to activate
    internal void PressChordKeys(IReadOnlyList<uint> codes, bool down)
    {
        if (codes.Count == 0) return;
        EvdevKeyResolver? resolver;
        Action<KeyEvent>? onKeyEvent;
        lock (_lock)
        {
            resolver = _resolver;
            onKeyEvent = _onKeyEvent;
        }
        Type(resolver, onKeyEvent, codes, down);
    }

    private static void Type(EvdevKeyResolver? resolver, Action<KeyEvent>? onKeyEvent, IReadOnlyList<uint> keys, bool down)
    {
        if (resolver is null || onKeyEvent is null) return;
        foreach (var keyEvent in WaylandShortcuts.Events(resolver, keys, down)) onKeyEvent(keyEvent);
    }

    // -- the keyboards, the hotkeys at home in place of the shortcuts, on the key source's thread --

    // at home the router gets every key as typed; during a capture EIS brings them, so only the release of a key pressed
    // at home (the chord that began the visit) comes this way, and goes home. EIS's up for it is dropped: neither its
    // resolver nor the router saw a down for it
    private void OnHomeKey(uint code, bool pressed)
    {
        EvdevKeyResolver? resolver;
        Action<KeyEvent>? onKeyEvent;
        lock (_lock)
        {
            if (!_tapped) return;
            if (Captured)
            {
                if (pressed || !_homeKeysHeld.Remove(code)) return;
            }
            else if (pressed) _homeKeysHeld.Add(code);
            else _homeKeysHeld.Remove(code);
            resolver = _homeResolver;
            onKeyEvent = _onKeyEvent;
        }
        Type(resolver, onKeyEvent, [code], pressed);
    }

    // -- the pure parts --

    // records a button against those pressed this capture; false for an up with no down here, which is dropped
    internal static bool TrackButton(HashSet<uint> held, uint code, bool pressed)
    {
        if (!pressed) return held.Remove(code);
        held.Add(code);
        return true;
    }

    // EIS counts 120 a notch like the wire, but down and left are positive there, as on a kernel wheel neither is: the
    // wire's Y is up, so it flips; its X is right, as EIS's
    internal static MouseScrollEvent DiscreteScroll(int dx, int dy) =>
        new(ToShort(dx), ToShort(-(long)dy));

    // a smooth delta in notch units, the fraction too small to send carried to the next
    internal static short SmoothScroll(double delta, ref double carry)
    {
        var units = delta * MouseScrollEvent.Notch / EiDeltaPerNotch + carry;
        if (!double.IsFinite(units))
        {
            carry = 0;
            return 0;
        }
        var whole = Math.Clamp(Math.Truncate(units), short.MinValue, short.MaxValue);
        carry = units - whole;
        return (short)whole;
    }

    private static short ToShort(long value) => (short)Math.Clamp(value, short.MinValue, short.MaxValue);

    // a capture the compositor started: the barrier it hit (null when none is known), where the router was told it hit, and
    // the serial the router's answer names
    private sealed record Activation(uint Id, BarrierLine? Barrier, ScreenPoint Hit, long Serial);

    // a release counted as queued: the portal it goes to, and the tap's token
    private sealed record ReleaseClaim(ICapturePortal Portal, CancellationToken Cancel);
}
