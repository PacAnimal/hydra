using System.IO.Hashing;
using Cathedral.Extensions;
using Hydra.Platform;
using Microsoft.Extensions.Logging;

namespace Hydra.Relay;

// a pending stream Drop, Retain or DropAll gave up on: its host was waiting for it
public sealed record DroppedClipboardStream(string Host, long Id);

// a stream given up on: its host, its id, the pull it answered (null for a push), and whether its sender gave up, which
// needs no abort back
public sealed record FailedClipboardStream(string Host, long Id, int? PullId = null, bool SenderGaveUp = false);

// what the receiver's owner did with its clipboard as a stream began: emptied it, failed to (tried again on each chunk),
// or kept it (copied files, or a clipboard already the sender's)
public enum BeginDecision { Cleared, ClearFailed, Keep }

// reassembles clipboard messages a peer streams in chunks; the clipboard stays empty from Begin until the whole message lands,
// unless the owner kept it (BeginDecision)
// a stream that fails (watchdog, bad data, rejected Begin, unreadable message) is logged as a warning, leaves the clipboard
// empty and raises Failed, so its sender is Aborted and FailedOsd shown; only accepts streams of one inner kind
// begun gets the host and the Begin's content hash; rejected the host of a Begin rejected, which empties only Hydra's
// own write
public sealed class ClipboardStreamReceiver(IClipboardSync sync, Func<string, ulong, BeginDecision> begun, Func<string, BeginDecision> rejected,
    TimeProvider clock, ILogger log, MessageKind accepts = MessageKind.ClipboardPush)
    : IDisposable
{
    public const string FailedOsd = "Clipboard Transfer Failed";

    // past the relay's stall drop, so a sender held behind another host's stalled download is dropped before this fires
    public static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(Styx.Constants.PeerWriteDeadlineSeconds + 15);

    // the blob is the encoded message, whose bytes travel as base64 inside JSON, so it may outgrow the clipboard cap; the
    // sender trims to fit it (ClipboardEncoding)
    public static readonly int MaxTotalBytes = (int)(ClipboardUtils.MaxClipboardBytes * 2);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, IncomingStream> _byHost = new(StringComparer.OrdinalIgnoreCase);
    // the last host to start a clipboard and its stream's id, null until it begins one; the hosts it superseded wait
    // for that stream. under _lock
    private string? _winner;
    private long? _winnerStream;
    private readonly HashSet<string> _heldOff = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    // the clipboard changed here mid-stream, whose sender should stop: host, id
    public event Action<string, long>? Abandoned;

    // a stream given up on as failed, whose sender should stop unless it gave up itself, and whose failure is shown; a
    // sender's abort is a supersede unless it says it failed
    public event Action<FailedClipboardStream>? Failed;

    // whether begin's stream was accepted and is pending
    public bool OnBegin(string host, ClipboardStreamBeginMessage begin)
    {
        if (begin.Inner != accepts || begin.TotalBytes <= 0 || begin.TotalBytes > MaxTotalBytes)
        {
            Reject(host, begin);
            return false;
        }

        var stream = new IncomingStream(begin);
        lock (_lock)
        {
            if (_disposed) return false;
            // the winner's newest stream is the one the hosts it superseded wait for, unless that wait already ended
            if (host.EqualsIgnoreCase(_winner))
            {
                if (_winnerStream == null || !WinnerGone()) _winnerStream = begin.Id;
                else EndHold();
            }
            Remove(host);
            _byHost[host] = stream;
            stream.Timer = clock.CreateTimer(_ => TimedOut(host, stream), null, Watchdog, Timeout.InfiniteTimeSpan);
        }
        var decision = begun(host, begin.ContentHash);
        bool pending;
        lock (_lock)
        {
            stream.Decision = decision;
            pending = _byHost.TryGetValue(host, out var current) && current == stream;
        }
        if (decision == BeginDecision.ClearFailed)
            log.LogWarning("Clipboard stream {Id} from {Host}: the clipboard could not be emptied; trying again with each chunk", begin.Id, host);
        if (log.IsEnabled(LogLevel.Debug))
            log.LogDebug("Clipboard stream {Id} from {Host} begun: {Inner}, {TotalBytes} bytes, {Decision}",
                begin.Id, host, begin.Inner, begin.TotalBytes, decision);
        return pending;
    }

    // the completed, verified inner message, or null while incomplete or on any failure
    public DecodedMessage? OnChunk(string host, ClipboardStreamChunkMessage chunk)
    {
        var ours = Ours(host, chunk.Id);
        IncomingStream? complete;
        IncomingStream? failed = null;
        lock (_lock)
        {
            if (_disposed) return null;
            if (!_byHost.TryGetValue(host, out var stream) || stream.Begin.Id != chunk.Id)
            {
                if (log.IsEnabled(LogLevel.Debug))
                    log.LogDebug("Dropped clipboard stream chunk {Id} at {Offset} from {Host}: no such stream", chunk.Id, chunk.Offset, host);
                return null;
            }
            // an empty chunk is bad data, never a keep-alive; json can leave Data null
            if (chunk.Data is not { Length: > 0 } data || chunk.Offset != stream.Received || data.Length > stream.Begin.TotalBytes - stream.Received)
            {
                failed = Remove(host);
                log.LogWarning("Dropped clipboard stream {Id} from {Host}: chunk of {Length} bytes at {Offset}, expected {Received} of {TotalBytes}",
                    chunk.Id, host, chunk.Data is { } bad ? bad.Length : 0, chunk.Offset, stream.Received, stream.Begin.TotalBytes);
                complete = null;
            }
            else if (!ours)
            {
                Remove(host);
                complete = null;
            }
            else
            {
                stream.Append(data);
                stream.Timer?.Change(Watchdog, Timeout.InfiniteTimeSpan);
                if (stream.Received < stream.Begin.TotalBytes) return null;
                Remove(host);
                complete = stream;
            }
        }

        if (!ours)
        {
            if (log.IsEnabled(LogLevel.Debug))
                log.LogDebug("Abandoned clipboard stream {Id} from {Host}: the clipboard changed here", chunk.Id, host);
            if (!Volatile.Read(ref _disposed)) Abandoned?.Invoke(host, chunk.Id);
            return null;
        }
        if (complete != null) return Finish(host, complete);
        RaiseFailed(new FailedClipboardStream(host, chunk.Id, failed?.Begin.PullId));
        return null;
    }

    // the completed message would not parse as its kind: as much a failure as bad data
    public void OnUnreadable(string host, long id)
    {
        if (Volatile.Read(ref _disposed)) return;
        log.LogWarning("Dropped clipboard stream {Id} from {Host}: its message would not parse", id, host);
        RaiseFailed(new FailedClipboardStream(host, id));
    }

    // the sender superseded or cancelled its stream: forget it, the clipboard stays empty until its newer message lands;
    // one the sender gave up on is a failure, shown, with no abort back
    public void OnAbort(string host, ClipboardStreamAbortMessage abort)
    {
        IncomingStream? stream;
        lock (_lock)
        {
            if (_disposed || !_byHost.TryGetValue(host, out stream) || stream.Begin.Id != abort.Id) return;
            Remove(host);
        }
        if (abort.Failed == true)
        {
            log.LogWarning("Clipboard stream {Id} from {Host}: the sender gave up after {Received} of {TotalBytes} bytes; the clipboard stays empty",
                abort.Id, host, stream.Received, stream.Begin.TotalBytes);
            RaiseFailed(new FailedClipboardStream(host, abort.Id, stream.Begin.PullId, SenderGaveUp: true));
            return;
        }
        if (log.IsEnabled(LogLevel.Debug))
            log.LogDebug("Clipboard stream {Id} from {Host} aborted by the sender", abort.Id, host);
    }

    // the content hash of the stream pending from host, or null
    public ulong? PendingFrom(string host)
    {
        lock (_lock)
            return _byHost.TryGetValue(host, out var stream) ? stream.Begin.ContentHash : null;
    }

    // forget the stream from host, the clipboard stays empty; the stream dropped, if any, for the caller to tell or not
    public DroppedClipboardStream? Drop(string host)
    {
        lock (_lock)
            return _disposed ? null : Dropped(host, Remove(host));
    }

    // peers changed: forget the streams of hosts that left, the clipboard stays empty; the streams dropped
    public IReadOnlyList<DroppedClipboardStream> Retain(IReadOnlySet<string> present) =>
        DropWhere(h => !present.Contains(h, StringComparer.OrdinalIgnoreCase));

    // a clipboard from host starts: the last to start wins, so every other host's stream is dropped, and those hosts are
    // held off while the stream host begins next is pending; the streams dropped, for the caller to tell or not
    public IReadOnlyList<DroppedClipboardStream> Supersede(string host)
    {
        lock (_lock)
        {
            if (_disposed) return [];
            // hosts a replaced winner held off stay held off until the newest stream ends
            if (WinnerGone()) EndHold();
            // a winner starting again keeps its pending stream until OnBegin replaces it
            if (!host.EqualsIgnoreCase(_winner))
            {
                _winner = host;
                _winnerStream = null;
            }
            var dropped = DropWhereLocked(h => !h.EqualsIgnoreCase(host));
            _heldOff.UnionWith(dropped.Select(d => d.Host));
            _heldOff.Remove(host);
            return dropped;
        }
    }

    // the winner whose pending stream holds host off, or null; once that stream ends, completed or not, none is held off
    public string? HeldOffBy(string host)
    {
        lock (_lock)
        {
            if (WinnerGone()) EndHold();
            return _heldOff.Contains(host) ? _winner : null;
        }
    }

    // dormancy or disconnect; the streams dropped
    public IReadOnlyList<DroppedClipboardStream> DropAll() => DropWhere(_ => true);

    private List<DroppedClipboardStream> DropWhere(Func<string, bool> gone)
    {
        lock (_lock)
        {
            return _disposed ? [] : DropWhereLocked(gone);
        }
    }

    // under _lock: forgets the streams of the hosts that match; the streams dropped
    private List<DroppedClipboardStream> DropWhereLocked(Func<string, bool> gone) =>
        [.. _byHost.Keys.Where(gone).ToList().Select(host => Dropped(host, Remove(host))!)];

    // logs each stream dropped, whose clipboard stays empty; whether there were any, for the caller to show
    public static bool LogDropped(IReadOnlyList<DroppedClipboardStream> dropped, string why, ILogger log)
    {
        foreach (var stream in dropped)
            log.LogWarning("Clipboard stream {Id} from {Host} dropped: {Why}; the clipboard stays empty", stream.Id, stream.Host, why);
        return dropped.Count > 0;
    }

    // from here on everything is ignored: no timer armed, no event raised
    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            foreach (var stream in _byHost.Values)
                stream.Timer?.Dispose();
            _byHost.Clear();
        }
    }

    private static DroppedClipboardStream? Dropped(string host, IncomingStream? stream) =>
        stream == null ? null : new DroppedClipboardStream(host, stream.Begin.Id);

    // still the receiver was told a newer clipboard is coming, so its owner empties (only Hydra's own write, a copy made
    // since being newer than whatever this Begin said), the older stream is forgotten and the sender stopped
    private void Reject(string host, ClipboardStreamBeginMessage begin)
    {
        lock (_lock)
        {
            if (_disposed) return;
            Remove(host);
        }
        if (rejected(host) == BeginDecision.ClearFailed)
            log.LogWarning("Rejected clipboard stream from {Host}: the clipboard could not be emptied", host);
        log.LogWarning("Rejected clipboard stream {Id} from {Host}: {Inner}, {TotalBytes} bytes", begin.Id, host, begin.Inner, begin.TotalBytes);
        RaiseFailed(new FailedClipboardStream(host, begin.Id, begin.PullId));
    }

    private DecodedMessage? Finish(string host, IncomingStream stream)
    {
        var blob = stream.Buffer;
        if (stream.Hash.GetCurrentHashAsUInt64() != stream.Begin.BlobHash || blob[0] != (byte)stream.Begin.Inner)
        {
            log.LogWarning("Dropped clipboard stream {Id} from {Host}: its {TotalBytes} bytes don't match their Begin", stream.Begin.Id, host, blob.Length);
            RaiseFailed(new FailedClipboardStream(host, stream.Begin.Id, stream.Begin.PullId));
            return null;
        }
        if (log.IsEnabled(LogLevel.Debug))
            log.LogDebug("Clipboard stream {Id} from {Host} complete: {TotalBytes} bytes", stream.Begin.Id, host, blob.Length);
        return MessageSerializer.Decode(blob);
    }

    private void TimedOut(string host, IncomingStream stream)
    {
        lock (_lock)
        {
            if (_disposed || !_byHost.TryGetValue(host, out var current) || current != stream) return;
            Remove(host);
        }
        log.LogWarning("Clipboard stream {Id} from {Host} timed out after {Received} of {TotalBytes} bytes",
            stream.Begin.Id, host, stream.Received, stream.Begin.TotalBytes);
        RaiseFailed(new FailedClipboardStream(host, stream.Begin.Id, stream.Begin.PullId));
    }

    private void RaiseFailed(FailedClipboardStream failed)
    {
        if (!Volatile.Read(ref _disposed)) Failed?.Invoke(failed);
    }

    // under _lock; a watchdog callback already in flight finds its stream gone and does nothing
    private IncomingStream? Remove(string host)
    {
        if (!_byHost.Remove(host, out var stream)) return null;
        stream.Timer?.Dispose();
        return stream;
    }

    // under _lock; whether the winner's stream, if any, has ended, completed or not
    private bool WinnerGone() =>
        _winner == null || !_byHost.TryGetValue(_winner, out var stream) || stream.Begin.Id != _winnerStream;

    // under _lock; no host is held off any more
    private void EndHold()
    {
        _winner = null;
        _winnerStream = null;
        _heldOff.Clear();
    }

    // whether the clipboard is still ours for the stream this chunk is of: a kept clipboard is never looked at, and one
    // not yet emptied is ours to blame, so the clear is tried again and no user copy looked for until it took
    private bool Ours(string host, long id)
    {
        IncomingStream? stream;
        BeginDecision before;
        lock (_lock)
        {
            if (_disposed || !_byHost.TryGetValue(host, out stream) || stream.Begin.Id != id) return true;
            before = stream.Decision;
        }
        // the platform is asked outside _lock, which it must never wait under
        if (before != BeginDecision.ClearFailed) return before == BeginDecision.Keep || sync.CanUseEchoFallback();
        var decision = begun(host, stream.Begin.ContentHash);
        lock (_lock)
            if (_byHost.TryGetValue(host, out var current) && current == stream) stream.Decision = decision;
        if (decision != BeginDecision.ClearFailed && log.IsEnabled(LogLevel.Debug))
            log.LogDebug("Clipboard stream {Id} from {Host}: the clipboard is {Decision} now", id, host, decision);
        return true;
    }

    private sealed class IncomingStream(ClipboardStreamBeginMessage begin)
    {
        public ClipboardStreamBeginMessage Begin { get; } = begin;
        // what the owner did with the clipboard at Begin, under _lock; a chunk racing the decision finds it kept
        public BeginDecision Decision { get; set; } = BeginDecision.Keep;
        // grown as chunks arrive, never past TotalBytes, so a Begin alone allocates nothing
        public byte[] Buffer { get; private set; } = [];
        public XxHash64 Hash { get; } = new();
        public int Received { get; private set; }
        public ITimer? Timer { get; set; }

        // the caller has checked data fits within TotalBytes
        public void Append(byte[] data)
        {
            var needed = Received + data.Length;
            if (needed > Buffer.Length)
            {
                var buffer = Buffer;
                Array.Resize(ref buffer, (int)Math.Min(Begin.TotalBytes, Math.Max(needed, (long)Buffer.Length * 2)));
                Buffer = buffer;
            }
            data.CopyTo(Buffer, Received);
            Hash.Append(data);
            Received = needed;
        }
    }
}
