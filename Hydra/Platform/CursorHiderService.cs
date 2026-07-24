using Cathedral.Utils;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform;

public interface ICursor
{
    ValueTask HideCursor();
    ValueTask ShowCursor();
    void WarpCursor(int x, int y) { }
    (int X, int Y)? GetCursorPosition() => null;
    bool CursorIsVisible => false;
}

public interface ICursorHider
{
    void Hide();
    void Show();
    void UpdateWarpPoint(int x, int y) { }
}

public sealed class CursorHiderService(ILogger<CursorHiderService> log, IPlatformInput platform)
    : SimpleHostedService(log, TimeSpan.FromSeconds(1)), ICursorHider
{
    private const int LocalPollMs = 100;
    private const int LocalTimeoutMs = 5000;

    // all mutable state below is guarded by _stateLock. Hide()/Show()/UpdateWarpPoint run on caller threads,
    // Execute on the hosted-service loop, and OnPoll/OnLocalTimeout on Timer threads — without the lock the
    // non-atomic multi-flag flips raced into stuck states (cursor stuck invisible) and the timer fields
    // leaked. Platform calls (HideCursor/ShowCursor await) are made OUTSIDE the lock.
    private readonly Lock _stateLock = new();
    private bool _hideIntent;
    private bool _localActive;
    private bool _pendingHide;
    private bool _pendingShow;
    private bool _hasWarpPoint;
    private int _warpX;
    private int _warpY;

    private (int X, int Y)? _lastPosition;
    private Timer? _pollTimer;
    private Timer? _localTimeoutTimer;

    public void Hide()
    {
        lock (_stateLock)
        {
            _hideIntent = true;
            _localActive = false;
            _pendingShow = false;
            _pendingHide = true;
            StopLocalTimeoutLocked();
            StartPollLocked();
        }
        Trigger();
    }

    public void Show()
    {
        lock (_stateLock)
        {
            _hideIntent = false;
            _localActive = false;
            _pendingHide = false;
            _pendingShow = true;
            StopPollLocked();
        }
        Trigger();
    }

    public void UpdateWarpPoint(int x, int y)
    {
        lock (_stateLock) { _warpX = x; _warpY = y; _hasWarpPoint = true; }
    }

    protected override async Task Execute(CancellationToken cancel)
    {
        // decide the action (and consume the pending flag) atomically under the lock, then run the platform
        // call outside it — never hold the lock across an await.
        bool doHide = false, doShow = false, doWarp = false;
        int warpX = 0, warpY = 0;
        lock (_stateLock)
        {
            if (_pendingHide)
            {
                _pendingHide = false;
                doHide = true;
            }
            else if (_pendingShow)
            {
                _pendingShow = false;
                doShow = true;
            }
            else if (_hideIntent && !_localActive && _hasWarpPoint)
            {
                doWarp = true;
                warpX = _warpX;
                warpY = _warpY;
            }
        }

        if (doHide)
        {
            await platform.HideCursor();
        }
        else if (doShow)
        {
            await platform.ShowCursor();
        }
        else if (doWarp)
        {
            // keep cursor pinned at warp point while hidden — don't warp when temporarily shown.
            platform.WarpCursor(warpX, warpY);
            lock (_stateLock) _lastPosition = (warpX, warpY); // set after the physical warp (matches OnPoll's baseline)
            // re-hide if cursor became visible — on macOS CGDisplayHideCursor is reference-counted
            // and can be decremented externally; CursorIsVisible detects that so we only re-hide when needed.
            if (platform.CursorIsVisible)
                await platform.HideCursor();
        }
    }

    protected override async Task OnShutdown(CancellationToken cancel)
    {
        lock (_stateLock) StopPollLocked();
        await platform.ShowCursor();
    }

    // -- timer helpers: caller must hold _stateLock --

    private void StartPollLocked()
    {
        StopPollLocked();
        if (platform.GetCursorPosition() == null) return;
        _lastPosition = null;  // first poll establishes baseline after any pending warps settle
        _pollTimer = new Timer(OnPoll, null, LocalPollMs, LocalPollMs);
    }

    private void StopPollLocked()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
        StopLocalTimeoutLocked();
    }

    private void StartLocalTimeoutLocked()
    {
        _localTimeoutTimer?.Dispose();
        _localTimeoutTimer = new Timer(OnLocalTimeout, null, LocalTimeoutMs, Timeout.Infinite);
    }

    private void StopLocalTimeoutLocked()
    {
        _localTimeoutTimer?.Dispose();
        _localTimeoutTimer = null;
    }

    private void OnPoll(object? _)
    {
        lock (_stateLock)
        {
            if (!_hideIntent) return;
            if (platform.IsOnVirtualScreen) return;
            var current = platform.GetCursorPosition();
            if (current == null) return;
            var last = _lastPosition;
            _lastPosition = current;
            if (last == null || current == last) return;
            if (_localActive)
            {
                // already showing — just reset the inactivity timeout
                StartLocalTimeoutLocked();
                return;
            }
            _localActive = true;
            _pendingHide = false;
            _pendingShow = true;
            StartLocalTimeoutLocked();
        }
        // reached only when we just transitioned to local-active (every other path returned)
        log.LogDebug("Cursor visible (local activity)");
        Trigger();
    }

    private void OnLocalTimeout(object? _)
    {
        lock (_stateLock)
        {
            if (!_hideIntent) return;
            _localActive = false;
            _pendingShow = false;
            _pendingHide = true;
        }
        log.LogDebug("Cursor hidden (local inactivity)");
        Trigger();
    }
}
