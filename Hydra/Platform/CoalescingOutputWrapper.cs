using System.Collections.Concurrent;
using Hydra.Relay;

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
    private bool _disposed;

    public CoalescingOutputWrapper(IPlatformOutput inner) : this(inner, runDrainThread: true) { }

    // runDrainThread: false leaves draining to the caller via DrainPending() — used by tests to drive
    // delivery deterministically instead of racing the background thread against a sleep.
    internal CoalescingOutputWrapper(IPlatformOutput inner, bool runDrainThread)
    {
        _inner = inner;
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
                _actions.TryAdd(() => FlushMove(batch));
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
        try { foreach (var action in _actions.GetConsumingEnumerable()) action(); }
        catch (InvalidOperationException) { } // thrown by BlockingCollection when CompleteAdding races with enumeration start
    }

    // drains every currently-queued action on the caller's thread. only valid when constructed with
    // runDrainThread: false (no background drainer to race against) — the test seam for deterministic delivery.
    internal void DrainPending()
    {
        while (_actions.TryTake(out var action)) action();
    }

    internal int PendingActionCount => _actions.Count;

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
            _drainThread.Join();
        else
            DrainPending(); // manual mode: flush the queue inline so a pending move is still delivered
        _inner.Dispose();
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
