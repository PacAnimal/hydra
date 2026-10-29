using System.IO.Hashing;
using Cathedral.Extensions;
using Hydra.Platform;
using Microsoft.Extensions.Logging;

namespace Hydra.Relay;

// sends clipboard messages, streaming any over Threshold so input never queues behind them; one stream per host, the
// cursor's host first
public sealed class ClipboardStreamSender : IAsyncDisposable
{
    public const int Threshold = 512 * 1024;
    public const int ChunkBytes = 256 * 1024;

    private readonly IRelaySender _relay;
    private readonly Func<string?> _priorityHost;
    private readonly ILogger _log;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, OutgoingStream> _byHost = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<OutgoingStream> _rotation = [];
    // messages handed to Send still to be encoded by the loop, oldest first; at most one per host but an unchanged
    // answer waiting behind another
    private readonly List<Queued> _queued = [];
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private long _nextId = Random.Shared.NextInt64();
    private int _turn;
    private bool _disposed;

    // tests: runs before each encode, to make one throw
    internal Action? EncodeFault { get; set; }

    public ClipboardStreamSender(IRelaySender relay, Func<string?> priorityHost, ILogger log)
    {
        _relay = relay;
        _priorityHost = priorityHost;
        _log = log;
        _loop = Task.Run(() => Run(_stop.Token));
    }

    // undelivered: told when the message could not be encoded and so never goes, for a peer awaiting it to be told
    public void Send<T>(string host, MessageKind kind, T message, Action? undelivered = null) where T : IClipboardPayload
    {
        if (kind is not (MessageKind.ClipboardPush or MessageKind.ClipboardPullResponse))
            throw new ArgumentException($"{kind} is not a clipboard message", nameof(kind));
        if (Volatile.Read(ref _disposed)) return;

        // only a message sure to go whole is encoded here, which is cheap; any other is encoded by the loop, off the
        // caller's thread (on a slave, the receive path that also applies keystrokes)
        var prepared = UpperBound(message) <= Threshold ? Prepare(kind, message, null) : null;
        var unchanged = message is ClipboardPullResponseMessage { Unchanged: true };
        lock (_lock)
        {
            if (_disposed) return;
            // a newer clipboard replaces any still waiting for the loop, as it would their streams (an unchanged answer
            // only a push), and stops the host's stream now, so its abort precedes whatever replaces it
            foreach (var replaced in _queued.Where(q => q.Host.EqualsIgnoreCase(host) && (!unchanged || q.Kind == MessageKind.ClipboardPush)).ToList())
            {
                _queued.Remove(replaced);
                _relay.SendClipboardAbort(host, replaced.Id);
            }
            // an unchanged answer behind a pull answer still waiting goes after it, in order
            var behind = _queued.Exists(q => q.Host.EqualsIgnoreCase(host));
            if (!behind) AbortReplacedLocked(host, unchanged);
            if (prepared != null && !behind)
            {
                SendOrStreamLocked(host, prepared);
                return;
            }
            var id = Interlocked.Increment(ref _nextId);
            _queued.Add(new Queued(host, kind, id, prepared != null ? () => prepared : () => Prepare(kind, message, id), undelivered));
            // inside the lock, so a dispose racing this never finds the wake disposed under it
            _wake.Release();
        }
    }

    // the encoded message, trimmed to the receiver's cap, with its stream's Begin when over Threshold
    private Prepared Prepare<T>(MessageKind kind, T message, long? queuedId) where T : IClipboardPayload
    {
        EncodeFault?.Invoke();
        var encoded = ClipboardEncoding.EncodeWithinCap(kind, message, _log);
        message = encoded.Message;
        var blob = encoded.Blob;
        var unchanged = message is ClipboardPullResponseMessage { Unchanged: true };
        if (blob.Length <= Threshold) return new Prepared(kind, blob, null, 0, unchanged);
        var id = queuedId ?? Interlocked.Increment(ref _nextId);
        // the hash a ClipboardHash or a pull carries for this clipboard, so a pending stream counts as applied
        var contentHash = ClipboardUtils.ClipboardHash(ClipboardWire.From(message, _log, "stream"));
        var response = message as ClipboardPullResponseMessage;
        var begin = new ClipboardStreamBeginMessage(id, kind, blob.Length, XxHash64.HashToUInt64(blob), contentHash, response?.PullId, response?.Resends);
        return new Prepared(kind, blob, MessageSerializer.Encode(MessageKind.ClipboardStreamBegin, begin), id, false);
    }

    // under the lock the loop hands each payload over in, so the older stream's abort follows anything it sent and
    // precedes whatever replaces it; an unchanged answer to a newer pull says the clipboard is still the one streaming,
    // which must go on landing
    private void AbortReplacedLocked(string host, bool unchanged)
    {
        if (unchanged && _byHost.TryGetValue(host, out var current) && current.Kind == MessageKind.ClipboardPullResponse) return;
        if (RemoveLocked(host) is { } old) _relay.SendClipboardAbort(host, old.Id);
    }

    private void SendOrStreamLocked(string host, Prepared prepared)
    {
        if (prepared.BeginPayload is not { } beginPayload)
        {
            _relay.Send([host], prepared.Blob);
            return;
        }
        var stream = new OutgoingStream(host, prepared.Id, prepared.Kind, prepared.Blob, beginPayload);
        _byHost[host] = stream;
        _rotation.Add(stream);
        _wake.Release();
    }

    // the oldest message waiting, encoded here off the caller's thread, then sent or streamed unless replaced or
    // dropped meanwhile
    private void Dispatch(Queued next)
    {
        Prepared prepared;
        try
        {
            prepared = next.Prepare();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Clipboard {Kind} to {Host} could not be encoded, dropping it", next.Kind, next.Host);
            bool dropped;
            lock (_lock) dropped = _queued.Remove(next);
            // one replaced or dropped meanwhile was never awaited
            if (dropped) next.Undelivered?.Invoke();
            return;
        }
        lock (_lock)
        {
            if (_disposed || !_queued.Remove(next)) return;
            AbortReplacedLocked(next.Host, prepared.Unchanged);
            SendOrStreamLocked(next.Host, prepared);
        }
    }

    // a bound on the encoded size: json escapes a char to at most 6 bytes, base64 is 4 per 3, plus the field names
    private static long UpperBound(IClipboardPayload m) =>
        1024 + 6L * ((m.Text?.Length ?? 0) + (m.PrimaryText?.Length ?? 0) + (m.Html?.Length ?? 0))
             + 4L * ((m.ImagePng?.Length ?? 0) + 2) / 3 + 4L * ((m.Rtf?.Length ?? 0) + 2) / 3;

    // the receiver stopped that stream
    public void OnAbort(string host, ClipboardStreamAbortMessage abort)
    {
        lock (_lock)
        {
            if (_disposed || !_byHost.TryGetValue(host, out var stream) || stream.Id != abort.Id) return;
            RemoveLocked(host);
        }
    }

    // the clipboard changed: stop every push, queued or streaming, and tell its receiver; the hosts whose push was
    // aborted, each once, to be offered the new clipboard
    public IReadOnlyList<string> AbortPushes()
    {
        lock (_lock)
        {
            if (_disposed) return [];
            HashSet<string> aborted = new(StringComparer.OrdinalIgnoreCase);
            foreach (var queued in _queued.Where(q => q.Kind == MessageKind.ClipboardPush).ToList())
            {
                _queued.Remove(queued);
                _relay.SendClipboardAbort(queued.Host, queued.Id);
                aborted.Add(queued.Host);
            }
            foreach (var stream in _byHost.Values.Where(s => s.Kind == MessageKind.ClipboardPush).ToList())
            {
                RemoveLocked(stream.Host);
                _relay.SendClipboardAbort(stream.Host, stream.Id);
                aborted.Add(stream.Host);
                if (_log.IsEnabled(LogLevel.Debug))
                    _log.LogDebug("Clipboard stream {Id} to {Host} aborted: the clipboard changed", stream.Id, stream.Host);
            }
            return [.. aborted];
        }
    }

    // dormancy: stop every stream and tell its receiver it failed, so none waits out its watchdog
    public void AbortAll() => DropWhere(_ => true, tellFailed: true);

    // peers changed: stop the streams to hosts that left, send nothing more
    public void Retain(IReadOnlySet<string> present) => DropWhere(h => !present.Contains(h, StringComparer.OrdinalIgnoreCase));

    // relay disconnected
    public void DropAll() => DropWhere(_ => true);

    private void DropWhere(Func<string, bool> gone, bool tellFailed = false)
    {
        lock (_lock)
        {
            if (_disposed) return;
            foreach (var stream in _rotation.Where(s => gone(s.Host)).ToList())
            {
                RemoveLocked(stream.Host);
                if (tellFailed) _relay.SendClipboardAbort(stream.Host, stream.Id, failed: true);
            }
            foreach (var queued in _queued.Where(q => gone(q.Host)).ToList())
            {
                _queued.Remove(queued);
                if (tellFailed) _relay.SendClipboardAbort(queued.Host, queued.Id, failed: true);
            }
        }
    }

    // from here on Send, OnAbort, AbortPushes, AbortAll, DropAll and Retain do nothing
    public async ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _queued.Clear();
            _byHost.Clear();
            _rotation.Clear();
        }
        await _stop.CancelAsync();
        try { await _loop; }
        catch (OperationCanceledException) { }
        _stop.Dispose();
        _wake.Dispose();
    }

    // one chunk at a time is in flight; while it is, queued messages are still encoded and handed over, so a host whose
    // chunk stalls holds up no other host's dispatch
    private async Task Run(CancellationToken cancel)
    {
        InFlight? inFlight = null;
        Task? woken = null;
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                // a message waiting goes before the next chunk, so a newer clipboard stops an older stream soon
                Queued? queued;
                lock (_lock) queued = _queued.Count > 0 ? _queued[0] : null;
                if (queued != null)
                {
                    Dispatch(queued);
                    continue;
                }
                if (inFlight is { Sent.IsCompleted: true } done)
                {
                    inFlight = null;
                    await Completed(done);
                    continue;
                }
                if (inFlight is null)
                {
                    inFlight = StartNext(cancel);
                    if (inFlight != null) continue;
                }
                // a wake left pending is kept for the next wait, never abandoned, so no release is lost
                woken ??= _wake.WaitAsync(cancel);
                if (inFlight is null) await woken;
                else await Task.WhenAny(woken, inFlight.Sent);
                if (woken.IsCompleted)
                {
                    var wake = woken;
                    woken = null;
                    await wake;
                }
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        // the step's send is cancelled with the loop, and its outcome no longer matters
        if (inFlight != null) await inFlight.Sent.ContinueWith(_ => { }, TaskScheduler.Default);
    }

    // the next step handed to the relay, or null when there is none
    private InFlight? StartNext(CancellationToken cancel)
    {
        var priority = _priorityHost();
        while (true)
        {
            Step? step;
            lock (_lock) step = NextLocked(priority);
            if (step is null) return null;
            var stream = step.Stream;
            var payload = step.Length is 0
                ? stream.BeginPayload
                : MessageSerializer.Encode(MessageKind.ClipboardStreamChunk,
                    new ClipboardStreamChunkMessage(stream.Id, step.Offset, stream.Blob.AsSpan(step.Offset, step.Length).ToArray()));
            // handed over under the lock, so a stream aborted or replaced since it was picked never sends again
            lock (_lock)
            {
                if (!IsCurrentLocked(stream)) continue;
                try
                {
                    return new InFlight(step, _relay.SendReliableAsync([stream.Host], payload, cancel).AsTask());
                }
                catch (Exception ex)
                {
                    return new InFlight(step, Task.FromException(ex));
                }
            }
        }
    }

    // a step's send finished: the stream moves on, or is dropped if it failed
    private async Task Completed(InFlight done)
    {
        var stream = done.Step.Stream;
        try
        {
            await done.Sent;
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Clipboard stream {Id} to {Host} failed, dropping it", stream.Id, stream.Host);
            lock (_lock)
            {
                if (!IsCurrentLocked(stream)) return;
                RemoveLocked(stream.Host);
                // best effort, so the receiver stops waiting rather than running out its watchdog; dropped if disconnected
                _relay.SendClipboardAbort(stream.Host, stream.Id, failed: true);
            }
            return;
        }
        lock (_lock)
        {
            if (!IsCurrentLocked(stream)) return;
            if (done.Step.Length is 0) stream.Begun = true;
            else stream.Offset = done.Step.Offset + done.Step.Length;
            if (stream.Offset == stream.Blob.Length) RemoveLocked(stream.Host);
        }
    }

    // the cursor's host if it has a stream, else the next in turn; Length 0 is the Begin
    private Step? NextLocked(string? priority)
    {
        if (_rotation.Count == 0) return null;
        if (priority is null || !_byHost.TryGetValue(priority, out var stream))
        {
            _turn %= _rotation.Count;
            stream = _rotation[_turn];
            _turn++;
        }
        if (!stream.Begun) return new Step(stream, 0, 0);
        return new Step(stream, stream.Offset, Math.Min(ChunkBytes, stream.Blob.Length - stream.Offset));
    }

    private bool IsCurrentLocked(OutgoingStream stream) => _byHost.TryGetValue(stream.Host, out var current) && current == stream;

    private OutgoingStream? RemoveLocked(string host)
    {
        if (!_byHost.Remove(host, out var stream)) return null;
        _rotation.Remove(stream);
        return stream;
    }

    private sealed class OutgoingStream(string host, long id, MessageKind kind, byte[] blob, byte[] beginPayload)
    {
        public string Host { get; } = host;
        public long Id { get; } = id;
        public MessageKind Kind { get; } = kind;
        public byte[] Blob { get; } = blob;
        public byte[] BeginPayload { get; } = beginPayload;
        public bool Begun { get; set; }
        public int Offset { get; set; }
    }

    private sealed record Step(OutgoingStream Stream, int Offset, int Length);

    // the one step handed to the relay and not yet finished
    private sealed record InFlight(Step Step, Task Sent);

    // a message Send left for the loop to encode; id is the stream it becomes, aborted as one not yet begun when
    // given up on first
    private sealed class Queued(string host, MessageKind kind, long id, Func<Prepared> prepare, Action? undelivered)
    {
        public string Host { get; } = host;
        public MessageKind Kind { get; } = kind;
        public long Id { get; } = id;
        public Func<Prepared> Prepare { get; } = prepare;
        public Action? Undelivered { get; } = undelivered;
    }

    // an encoded message: whole when BeginPayload is null, else the Begin of stream Id
    private sealed record Prepared(MessageKind Kind, byte[] Blob, byte[]? BeginPayload, long Id, bool Unchanged);
}
