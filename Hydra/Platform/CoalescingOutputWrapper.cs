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
    private bool _pendingAbsolute;
    private int _pendingAbsX, _pendingAbsY;
    private int _pendingRelDx, _pendingRelDy;
    private readonly BlockingCollection<Action> _actions = [];
    private readonly Thread _drainThread;

    // ReSharper disable once ConvertToPrimaryConstructor
#pragma warning disable IDE0290
    public CoalescingOutputWrapper(IPlatformOutput inner)
    {
        _inner = inner;
        _drainThread = new Thread(Drain) { IsBackground = true, Name = "output-coalescer" };
        _drainThread.Start();
    }
#pragma warning restore IDE0290

    public void MoveMouse(int x, int y)
    {
        lock (_moveLock)
        {
            _pendingAbsolute = true;
            _pendingAbsX = x;
            _pendingAbsY = y;
            // absolute overrides any accumulated relative
            _pendingRelDx = 0;
            _pendingRelDy = 0;
        }
        _actions.TryAdd(FlushMove);
    }

    public void MoveMouseRelative(int dx, int dy)
    {
        Action? flushPrevAbsolute = null;
        lock (_moveLock)
        {
            if (_pendingAbsolute)
            {
                // cannot merge relative into an absolute; queue the absolute and start fresh relative
                var (ax, ay) = (_pendingAbsX, _pendingAbsY);
                _pendingAbsolute = false;
                flushPrevAbsolute = () => _inner.MoveMouse(ax, ay);
            }
            _pendingRelDx += dx;
            _pendingRelDy += dy;
        }
        if (flushPrevAbsolute != null) _actions.TryAdd(flushPrevAbsolute);
        _actions.TryAdd(FlushMove);
    }

    public void InjectKey(KeyEventMessage msg)
    {
        FlushPendingMoveToQueue();
        _actions.Add(() => _inner.InjectKey(msg));
    }

    public void InjectMouseButton(MouseButtonMessage msg)
    {
        FlushPendingMoveToQueue();
        _actions.Add(() => _inner.InjectMouseButton(msg));
    }

    public void InjectMouseScroll(MouseScrollMessage msg)
    {
        FlushPendingMoveToQueue();
        _actions.Add(() => _inner.InjectMouseScroll(msg));
    }

    // drains any pending move into the action queue, in order before the non-move event.
    // called on the producer thread so the queued flush precedes the non-move event.
    private void FlushPendingMoveToQueue()
    {
        bool abs;
        int x = 0, y = 0, dx = 0, dy = 0;
        lock (_moveLock)
        {
            abs = _pendingAbsolute;
            if (abs) { x = _pendingAbsX; y = _pendingAbsY; _pendingAbsolute = false; }
            else if (_pendingRelDx != 0 || _pendingRelDy != 0)
            {
                dx = _pendingRelDx; dy = _pendingRelDy;
                _pendingRelDx = 0; _pendingRelDy = 0;
            }
            else return; // nothing pending
        }
        _actions.Add(abs ? (() => _inner.MoveMouse(x, y)) : (() => _inner.MoveMouseRelative(dx, dy)));
    }

    // called from the drain thread only; takes the pending move and delivers it
    private void FlushMove()
    {
        bool abs;
        int x = 0, y = 0, dx = 0, dy = 0;
        lock (_moveLock)
        {
            abs = _pendingAbsolute;
            if (abs) { x = _pendingAbsX; y = _pendingAbsY; _pendingAbsolute = false; }
            else if (_pendingRelDx != 0 || _pendingRelDy != 0)
            {
                dx = _pendingRelDx; dy = _pendingRelDy;
                _pendingRelDx = 0; _pendingRelDy = 0;
            }
            else return;
        }
        if (abs) _inner.MoveMouse(x, y);
        else _inner.MoveMouseRelative(dx, dy);
    }

    private void Drain()
    {
        try { foreach (var action in _actions.GetConsumingEnumerable()) action(); }
        catch (InvalidOperationException) { } // thrown by BlockingCollection when CompleteAdding races with enumeration start
    }

    public void Dispose()
    {
        FlushPendingMoveToQueue(); // deliver any final pending move
        _actions.CompleteAdding();
        _drainThread.Join(1000);
        _inner.Dispose();
    }
}
