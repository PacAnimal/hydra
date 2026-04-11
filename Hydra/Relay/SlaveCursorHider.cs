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

public sealed class SlaveCursorHider(ICursorVisibility cursor, ILogger<SlaveCursorHider> log, int pollIntervalMs = 100, int localTimeoutMs = 5000) : IDisposable
{
    private readonly ICursorVisibility _cursor = cursor;
    private readonly ILogger<SlaveCursorHider> _log = log;
    private readonly int _pollIntervalMs = pollIntervalMs;
    private readonly int _localTimeoutMs = localTimeoutMs;
    private readonly Lock _lock = new();

    private SlaveCursorState _state = SlaveCursorState.NoMaster;
    private CursorPosition? _lastPolledPosition;
    private Timer? _pollTimer;
    private Timer? _localTimeoutTimer;
    private int _masterCount;

    // which masters are currently on this screen (entered but not left)
    private readonly HashSet<string> _onScreenMasters = new(StringComparer.OrdinalIgnoreCase);
    private string? _activeMaster;

    public SlaveCursorState State { get { lock (_lock) return _state; } }

    public void OnMasterConnected()
    {
        lock (_lock)
        {
            _masterCount++;
            if (_masterCount == 1)
                EnterHidden();
        }
    }

    public void OnMasterDisconnected(string masterHost)
    {
        lock (_lock)
        {
            _onScreenMasters.Remove(masterHost);
            var wasActive = string.Equals(_activeMaster, masterHost, StringComparison.OrdinalIgnoreCase);
            if (wasActive)
                _activeMaster = null;
            if (_masterCount > 0) _masterCount--;
            if (_masterCount == 0)
                EnterNoMaster();
            else if (wasActive && _state == SlaveCursorState.MasterActive)
                EnterHidden();
        }
    }

    public void OnEnterScreen(string masterHost)
    {
        lock (_lock)
        {
            _onScreenMasters.Add(masterHost);
            _activeMaster = masterHost;
            if (_state is SlaveCursorState.Hidden or SlaveCursorState.LocalActive)
                EnterMasterActive();
        }
    }

    public void OnLeaveScreen(string masterHost)
    {
        lock (_lock)
        {
            _onScreenMasters.Remove(masterHost);
            if (string.Equals(_activeMaster, masterHost, StringComparison.OrdinalIgnoreCase) && _state == SlaveCursorState.MasterActive)
                EnterHidden();
        }
    }

    // called on every input message from a master — if it's on-screen and we're hidden, make it active and show cursor
    public void OnMasterActivity(string masterHost)
    {
        lock (_lock)
        {
            if (!_onScreenMasters.Contains(masterHost)) return;
            _activeMaster = masterHost;
            if (_state is SlaveCursorState.Hidden or SlaveCursorState.LocalActive)
                EnterMasterActive();
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
        _onScreenMasters.Clear();
        _activeMaster = null;
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
