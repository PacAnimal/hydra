using System.Collections.Concurrent;
using Hydra.Relay;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hydra.Platform;

// platform-agnostic IPlatformOutput decorator that coalesces burst mouse moves:
// - absolute: keeps only the latest position
// - relative: accumulates deltas into a single move
// non-move events are queued in order, preceded by a flush of any pending move.
// a dedicated background thread drains the action queue.
public sealed class CoalescingOutputWrapper : IPlatformOutput
{
    private readonly IPlatformOutput _inner;
    private readonly Lock _moveLock = new();
    private MoveBatch? _openMoveBatch;
    private readonly BlockingCollection<Action> _actions = [];
    private readonly Thread? _drainThread;
    private readonly ILogger<CoalescingOutputWrapper> _log;
    private readonly TimeSpan _shutdownTimeout;
    private int _faultCount;
    private long _maxPendingActionCount;
    private int _nextBacklogWarning = 1024;
    private bool _disposed;

    public CoalescingOutputWrapper(IPlatformOutput inner, ILogger<CoalescingOutputWrapper>? log = null)
        : this(inner, runDrainThread: true, log: log) { }

    // runDrainThread: false leaves draining to the caller via DrainPending() — used by tests to drive
    // delivery deterministically instead of racing the background thread against a sleep.
    internal CoalescingOutputWrapper(IPlatformOutput inner, bool runDrainThread,
        ILogger<CoalescingOutputWrapper>? log = null, TimeSpan? shutdownTimeout = null)
    {
        _inner = inner;
        _log = log ?? NullLogger<CoalescingOutputWrapper>.Instance;
        _shutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(2);
        if (runDrainThread)
        {
            _drainThread = new Thread(Drain) { IsBackground = true, Name = "output-coalescer" };
            _drainThread.Start();
        }
    }

    public void MoveMouse(int x, int y)
    {
        PostMove(absolute: true, x, y);
    }

    public void MoveMouseRelative(int dx, int dy)
    {
        PostMove(absolute: false, dx, dy);
    }

    private void PostMove(bool absolute, int x, int y)
    {
        lock (_moveLock)
        {
            if (_disposed) return;
            if (_openMoveBatch == null || _openMoveBatch.Absolute != absolute)
            {
                var batch = new MoveBatch(absolute);
                _openMoveBatch = batch;
                if (_actions.TryAdd(() => FlushMove(batch))) RecordPendingDepth();
            }

            _openMoveBatch.Add(x, y);
        }
    }

    public void InjectKey(KeyEventMessage msg)
    {
        PostControl(() => _inner.InjectKey(msg));
    }

    public void InjectMouseButton(MouseButtonMessage msg)
    {
        PostControl(() => _inner.InjectMouseButton(msg));
    }

    public void InjectMouseScroll(MouseScrollMessage msg)
    {
        PostControl(() => _inner.InjectMouseScroll(msg));
    }

    // The batch action was queued when the batch opened. Sealing here ensures movement arriving after a
    // key/button/scroll creates a new action behind that control event instead of being folded ahead of it.
    private void PostControl(Action action)
    {
        lock (_moveLock)
        {
            if (_disposed) return;
            _openMoveBatch = null;
            _actions.Add(action);
            RecordPendingDepth();
        }
    }

    private void FlushMove(MoveBatch batch)
    {
        (int X, int Y) move;
        lock (_moveLock)
        {
            if (ReferenceEquals(_openMoveBatch, batch)) _openMoveBatch = null;
            move = batch.Snapshot();
        }
        if (batch.Absolute) _inner.MoveMouse(move.X, move.Y);
        else _inner.MoveMouseRelative(move.X, move.Y);
    }

    private void Drain()
    {
        try
        {
            foreach (var action in _actions.GetConsumingEnumerable()) ExecuteAction(action);
        }
        catch (Exception ex)
        {
            RecordFault(ex, "Output drain stopped unexpectedly");
        }
        finally
        {
            DisposeInner();
        }
    }

    // drains every currently-queued action on the caller's thread. only valid when constructed with
    // runDrainThread: false (no background drainer to race against) — the test seam for deterministic delivery.
    internal void DrainPending()
    {
        while (_actions.TryTake(out var action)) ExecuteAction(action);
    }

    internal int PendingActionCount => _actions.Count;
    internal int FaultCount => Volatile.Read(ref _faultCount);
    internal long MaxPendingActionCount => Interlocked.Read(ref _maxPendingActionCount);

    private void RecordPendingDepth()
    {
        var depth = _actions.Count;
        var max = Interlocked.Read(ref _maxPendingActionCount);
        while (depth > max)
        {
            var observed = Interlocked.CompareExchange(ref _maxPendingActionCount, depth, max);
            if (observed == max) break;
            max = observed;
        }

        var threshold = Volatile.Read(ref _nextBacklogWarning);
        if (depth < threshold || Interlocked.CompareExchange(ref _nextBacklogWarning, threshold * 2, threshold) != threshold)
            return;
        _log.LogWarning("Platform output backlog reached {Depth} actions; native output may be stalled", depth);
    }

    private void ExecuteAction(Action action)
    {
        try { action(); }
        catch (Exception ex) { RecordFault(ex, "Platform output action failed; continuing to drain"); }
    }

    private void RecordFault(Exception ex, string message)
    {
        Interlocked.Increment(ref _faultCount);
        _log.LogError(ex, "{Message}", message);
    }

    private void DisposeInner()
    {
        try { _inner.Dispose(); }
        catch (Exception ex) { RecordFault(ex, "Platform output disposal failed"); }
    }

    public bool IsAccessibilityTrusted() => _inner.IsAccessibilityTrusted();
    public Task WaitForAccessibilityTrusted(CancellationToken cancel) => _inner.WaitForAccessibilityTrusted(cancel);

    public void Dispose()
    {
        lock (_moveLock)
        {
            if (_disposed) return;
            _disposed = true;
            _openMoveBatch = null; // its batch action is already queued
            _actions.CompleteAdding();
        }
        if (_drainThread != null)
        {
            if (!_drainThread.Join(_shutdownTimeout))
                _log.LogWarning("Output drain did not stop within {TimeoutMs}ms; native output remains owned by the background worker",
                    _shutdownTimeout.TotalMilliseconds);
        }
        else
        {
            DrainPending(); // manual mode: flush the queue inline so a pending move is still delivered
            DisposeInner();
        }
    }

    private sealed class MoveBatch(bool absolute)
    {
        private int _x;
        private int _y;

        public bool Absolute { get; } = absolute;

        public void Add(int x, int y)
        {
            if (Absolute) { _x = x; _y = y; }
            else { _x += x; _y += y; }
        }

        public (int X, int Y) Snapshot() => (_x, _y);
    }
}
