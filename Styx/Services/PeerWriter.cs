using System.Collections.Concurrent;
using Common.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace Styx.Services;

/// <summary>
/// Every message the relay sends a peer, written one at a time in the order its place was reserved. A caller with nothing
/// ahead of it writes its own message, with no hop to another thread; one that finds a write in flight queues behind it, and
/// a backlog is drained off the caller's thread. A sender waits until its message is taken, which keeps its backlog on its
/// own side and bounds the queue by the number of writers; membership snapshots waiting next to each other collapse into the
/// latest.
///
/// <para>A peer whose download has stalled while its pings still arrive never trips the client timeout, so one with
/// messages waiting that takes none for <see cref="Deadline"/> is dropped. Every message taken restarts that clock, so a
/// slow peer that keeps taking them is never charged for a queue from many senders. Writes are never cancelled: a cancelled
/// write leaves its bytes queued for the peer, and aborting the connection ends the stuck write and everything behind it.</para>
/// </summary>
/// <param name="clock">its own, so that a test stepping the relay's clock through hours is not read as a stall</param>
public sealed class PeerWriter(HubLifetimeManager<StyxHub> hub, [FromKeyedServices(typeof(PeerWriter))] TimeProvider clock, ILogger<PeerWriter> log)
{
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(Constants.PeerWriteDeadlineSeconds);

    private readonly ConcurrentDictionary<string, Peer> _peers = new();

    // a message has joined a peer's queue
    internal event Action<string, string>? Queued;

    public void Track(HubCallerContext connection) =>
        _peers[connection.ConnectionId] = new Peer(connection, hub, clock, log, method => Queued?.Invoke(connection.ConnectionId, method));

    public void Forget(string connectionId)
    {
        if (_peers.TryRemove(connectionId, out var peer)) peer.Close();
    }

    // every target's place is reserved before any is written, so each takes it in the order it reached the relay, and a
    // slow target holds up only this sender
    public Task Receive(IReadOnlyList<string> connectionIds, string sourceHost, string sourceIp, byte[] payload)
    {
        object?[] args = [sourceHost, sourceIp, payload];
        var reserved = new Reservation[connectionIds.Count];
        for (var i = 0; i < reserved.Length; i++) reserved[i] = Reserve(connectionIds[i], nameof(IStyxClient.Receive), args, false);
        var deliveries = new Task[reserved.Length];
        for (var i = 0; i < reserved.Length; i++) deliveries[i] = reserved[i].Start();
        return Task.WhenAll(deliveries);
    }

    // a live snapshot waiting behind another collapses into it; one of a displacement pair never does
    public Task Peers(string connectionId, string[] hostNames, bool collapsible) =>
        Reserve(connectionId, nameof(IStyxClient.Peers), [hostNames], collapsible).Start();

    // queued, not waited on: whoever kicks a peer has no reason to wait behind that peer's queue
    public void Kick(string connectionId, string reason) =>
        _ = Logged(Reserve(connectionId, nameof(IStyxClient.Kicked), [reason], false).Start(), connectionId);

    private async Task Logged(Task kick, string connectionId)
    {
        try
        {
            await kick;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not kick {ConnectionId}", connectionId);
        }
    }

    // untracked is already gone, and the lifetime manager has nothing to write to
    private Reservation Reserve(string connectionId, string method, object?[] args, bool collapsible) =>
        _peers.TryGetValue(connectionId, out var peer) ? peer.Reserve(method, args, collapsible) : default;

    /// <summary>A place in a peer's order: queued behind a write in flight, or first, to be written by whoever starts it.</summary>
    private readonly record struct Reservation(Peer? Peer, Message? Behind, string Method, object?[] Args)
    {
        public Task Start() => Peer is null ? Task.CompletedTask : Behind?.Taken.Task ?? Peer.WriteNow(Method, Args);
    }

    private sealed class Message(string method, object?[] args, bool collapsible)
    {
        public string Method { get; } = method;
        public object?[] Args { get; set; } = args;
        public bool Collapsible { get; } = collapsible;
        public TaskCompletionSource Taken { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Peer
    {
        // the timer's own resolution, so a re-arm for a sliver never spins
        private static readonly TimeSpan MinimumWait = TimeSpan.FromMilliseconds(1);

        private readonly HubCallerContext _connection;
        private readonly HubLifetimeManager<StyxHub> _hub;
        private readonly TimeProvider _clock;
        private readonly ILogger _log;
        private readonly Action<string> _queued;
        private readonly ITimer _watchdog;
        private readonly Lock _lock = new();
        private readonly Queue<Message> _queue = new();
        // the last message queued, while it is still waiting
        private Message? _tail;
        // a write is in flight or reserved, so anything new queues behind it
        private bool _busy;
        private bool _closed;
        // queued, reserved or in flight
        private int _waiting;
        private long _lastProgress;

        public Peer(HubCallerContext connection, HubLifetimeManager<StyxHub> hub, TimeProvider clock, ILogger log, Action<string> queued)
        {
            _connection = connection;
            _hub = hub;
            _clock = clock;
            _log = log;
            _queued = queued;
            _watchdog = clock.CreateTimer(_ => Expire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        public Reservation Reserve(string method, object?[] args, bool collapsible)
        {
            Reservation reserved;
            lock (_lock)
            {
                if (_closed) return default;
                if (!_busy)
                {
                    _busy = true;
                    if (_waiting++ == 0) Rearm();
                    reserved = new Reservation(this, null, method, args);
                }
                else if (collapsible && _tail is { Collapsible: true } tail)
                {
                    // nothing waits between the two, so the peer misses no step by seeing only the latest
                    tail.Args = args;
                    reserved = new Reservation(this, tail, method, args);
                }
                else
                {
                    var behind = new Message(method, args, collapsible);
                    _queue.Enqueue(behind);
                    _tail = behind;
                    _waiting++;
                    reserved = new Reservation(this, behind, method, args);
                }
            }
            _queued(method);
            return reserved;
        }

        // the queue still drains, into a connection that is gone, so no caller is left waiting
        public void Close()
        {
            lock (_lock)
            {
                _closed = true;
                if (!_busy) _watchdog.Dispose();
            }
        }

        // the reservation was first, so its caller writes it: an idle peer costs no hop to another thread
        public async Task WriteNow(string method, object?[] args)
        {
            try
            {
                await _hub.SendConnectionAsync(_connection.ConnectionId, method, args);
            }
            finally
            {
                Progressed();
                // a backlog is never drained on the caller's thread, which would hold its own Send for others' messages
                if (!Idle()) _ = Task.Run(Drain);
            }
        }

        private async Task Drain()
        {
            do
            {
                Message message;
                lock (_lock)
                {
                    message = _queue.Dequeue();
                    if (_queue.Count == 0) _tail = null;
                }
                try
                {
                    await _hub.SendConnectionAsync(_connection.ConnectionId, message.Method, message.Args);
                    message.Taken.TrySetResult();
                }
                catch (Exception ex)
                {
                    message.Taken.TrySetException(ex);
                }
                finally
                {
                    Progressed();
                }
            } while (!Idle());
        }

        // true, and no longer busy, once nothing is queued
        private bool Idle()
        {
            lock (_lock)
            {
                if (_queue.Count > 0) return false;
                _busy = false;
                if (_closed) _watchdog.Dispose();
                return true;
            }
        }

        private void Progressed()
        {
            lock (_lock)
            {
                if (--_waiting > 0) Rearm();
                else _watchdog.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }

        private void Rearm()
        {
            _lastProgress = _clock.GetTimestamp();
            _watchdog.Change(Deadline, Timeout.InfiniteTimeSpan);
        }

        private void Expire()
        {
            lock (_lock)
            {
                if (_waiting == 0) return;
                // a timer may fire a little early by this clock, or as it was re-armed: wait out the rest
                var left = Deadline - _clock.GetElapsedTime(_lastProgress);
                if (left > TimeSpan.Zero)
                {
                    _watchdog.Change(left < MinimumWait ? MinimumWait : left, Timeout.InfiniteTimeSpan);
                    return;
                }
            }
            _log.LogWarning("Dropping {ConnectionId}: it took no message for {Seconds} s", _connection.ConnectionId, Constants.PeerWriteDeadlineSeconds);
            _connection.Abort();
        }
    }
}
