using Cathedral.Utils;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform;

public interface ICursor
{
    ValueTask HideCursor();
    ValueTask ShowCursor();
    void WarpCursor(int x, int y) { }
    (int X, int Y)? GetCursorPosition() => null;
}

public interface ICursorHider
{
    void Hide();
    void Show();
    void UpdateWarpPoint(int x, int y) { }
}

public sealed class CursorHiderService(ICursor cursor, ILogger<CursorHiderService> log)
    : SimpleHostedService(log, TimeSpan.FromSeconds(1)), ICursorHider
{
    private const int LocalPollMs = 100;
    private const int LocalTimeoutMs = 5000;

    private volatile bool _hideIntent;
    private volatile bool _localActive;
    private volatile bool _pendingHide;
    private volatile bool _pendingShow;
    private volatile int _warpX;
    private volatile int _warpY;

    private (int X, int Y)? _lastPosition;
    private Timer? _pollTimer;
    private Timer? _localTimeoutTimer;

    public void Hide()
    {
        _hideIntent = true;
        _localActive = false;
        _pendingShow = false;
        _pendingHide = true;
        StopLocalTimeout();
        StartPoll();
        Trigger();
    }

    public void Show()
    {
        _hideIntent = false;
        _localActive = false;
        _pendingHide = false;
        _pendingShow = true;
        StopPoll();
        Trigger();
    }

    public void UpdateWarpPoint(int x, int y) { _warpX = x; _warpY = y; }

    protected override async Task Execute(CancellationToken cancel)
    {
        if (_pendingHide)
        {
            _pendingHide = false;
            cursor.WarpCursor(_warpX, _warpY);
            await cursor.HideCursor();
        }
        else if (_pendingShow)
        {
            _pendingShow = false;
            await cursor.ShowCursor();
        }
        else if (_hideIntent)
        {
            cursor.WarpCursor(_warpX, _warpY);
        }
    }

    protected override async Task OnShutdown(CancellationToken cancel)
    {
        StopPoll();
        await cursor.ShowCursor();
    }

    private void StartPoll()
    {
        StopPoll();
        _lastPosition = cursor.GetCursorPosition();
        if (_lastPosition == null) return;
        _pollTimer = new Timer(OnPoll, null, LocalPollMs, LocalPollMs);
    }

    private void StopPoll()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
        StopLocalTimeout();
    }

    private void StartLocalTimeout()
    {
        _localTimeoutTimer?.Dispose();
        _localTimeoutTimer = new Timer(OnLocalTimeout, null, LocalTimeoutMs, Timeout.Infinite);
    }

    private void StopLocalTimeout()
    {
        _localTimeoutTimer?.Dispose();
        _localTimeoutTimer = null;
    }

    private void OnPoll(object? _)
    {
        if (!_hideIntent) return;
        var current = cursor.GetCursorPosition();
        if (current == null) return;
        var last = _lastPosition;
        _lastPosition = current;
        if (last == null || current == last) return;
        if (_localActive)
        {
            // already showing — just reset the inactivity timeout
            StartLocalTimeout();
            return;
        }
        _localActive = true;
        _pendingHide = false;
        _pendingShow = true;
        Trigger();
        log.LogDebug("Cursor visible (local activity)");
        StartLocalTimeout();
    }

    private void OnLocalTimeout(object? _)
    {
        if (!_hideIntent) return;
        _localActive = false;
        _pendingShow = false;
        _pendingHide = true;
        Trigger();
        log.LogDebug("Cursor hidden (local inactivity)");
    }
}
