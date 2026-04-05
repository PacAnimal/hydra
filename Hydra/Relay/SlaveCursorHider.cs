using Hydra.Platform;
using Microsoft.Extensions.Logging;

namespace Hydra.Relay;

public enum SlaveCursorState
{
    NoMaster,
    Hidden,
    MasterActive,
    LocalActive,
}

public sealed class SlaveCursorHider : IDisposable
{
    private readonly ICursorVisibility _cursor;
    private readonly ILogger<SlaveCursorHider> _log;
    private readonly int _pollIntervalMs;
    private readonly int _localTimeoutMs;
    private readonly object _lock = new();

    private SlaveCursorState _state = SlaveCursorState.NoMaster;
    private CursorPosition? _lastPolledPosition;
    private Timer? _pollTimer;
    private Timer? _localTimeoutTimer;
    private int _masterCount;

    public SlaveCursorState State { get { lock (_lock) return _state; } }

    public SlaveCursorHider(ICursorVisibility cursor, ILogger<SlaveCursorHider> log, int pollIntervalMs = 100, int localTimeoutMs = 5000)
    {
        _cursor = cursor;
        _log = log;
        _pollIntervalMs = pollIntervalMs;
        _localTimeoutMs = localTimeoutMs;
    }

    public void OnMasterConnected()
    {
        lock (_lock)
        {
            _masterCount++;
            if (_masterCount == 1)
                EnterHidden();
        }
    }

    public void OnMasterDisconnected()
    {
        lock (_lock)
        {
            if (_masterCount > 0) _masterCount--;
            if (_masterCount == 0)
                EnterNoMaster();
        }
    }

    public void OnEnterScreen()
    {
        lock (_lock)
        {
            if (_state is SlaveCursorState.Hidden or SlaveCursorState.LocalActive)
                EnterMasterActive();
        }
    }

    public void OnLeaveScreen()
    {
        lock (_lock)
        {
            if (_state == SlaveCursorState.MasterActive)
                EnterHidden();
        }
    }

    // -- state transitions --

    private void EnterHidden()
    {
        StopLocalTimeout();
        StopPoll();
        _cursor.HideCursor();
        _lastPolledPosition = _cursor.GetCursorPosition();
        _state = SlaveCursorState.Hidden;
        _log.LogDebug("Slave cursor hidden");
        StartPoll();
    }

    private void EnterMasterActive()
    {
        StopLocalTimeout();
        StopPoll();
        _cursor.ShowCursor();
        _state = SlaveCursorState.MasterActive;
        _log.LogDebug("Slave cursor visible (master active)");
    }

    private void EnterLocalActive()
    {
        _cursor.ShowCursor();
        _state = SlaveCursorState.LocalActive;
        _log.LogDebug("Slave cursor visible (local activity)");
        StartLocalTimeout();
    }

    private void EnterNoMaster()
    {
        StopLocalTimeout();
        StopPoll();
        _cursor.ShowCursor();
        _state = SlaveCursorState.NoMaster;
        _log.LogDebug("Slave cursor visible (no master)");
    }

    // -- timers --

    private void StartPoll() =>
        _pollTimer = new Timer(OnPoll, null, _pollIntervalMs, _pollIntervalMs);

    private void StopPoll()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
    }

    private void StartLocalTimeout() =>
        _localTimeoutTimer = new Timer(OnLocalTimeout, null, _localTimeoutMs, Timeout.Infinite);

    private void StopLocalTimeout()
    {
        _localTimeoutTimer?.Dispose();
        _localTimeoutTimer = null;
    }

    private void OnPoll(object? _)
    {
        CursorPosition current;
        try { current = _cursor.GetCursorPosition(); }
        catch { return; }

        lock (_lock)
        {
            if (_state is not (SlaveCursorState.Hidden or SlaveCursorState.LocalActive)) return;

            var last = _lastPolledPosition;
            _lastPolledPosition = current;

            if (last == null || (current.X == last.X && current.Y == last.Y)) return;

            if (_state == SlaveCursorState.Hidden)
            {
                EnterLocalActive();
            }
            else
            {
                // LocalActive: reset the timeout
                _localTimeoutTimer?.Change(_localTimeoutMs, Timeout.Infinite);
            }
        }
    }

    private void OnLocalTimeout(object? _)
    {
        lock (_lock)
        {
            if (_state == SlaveCursorState.LocalActive)
                EnterHidden();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            StopLocalTimeout();
            StopPoll();
            _cursor.ShowCursor();
        }
    }
}
