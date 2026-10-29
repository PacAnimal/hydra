using Cathedral.Extensions;
using Hydra.Platform;
using Microsoft.Extensions.Logging;

namespace Hydra.Relay;

// the master's side of the clipboard handshake, and what it keeps between the slaves' messages: the latest pull to each
// host, and the pull whose answer is streaming in
public sealed class ClipboardMaster(IClipboardSync sync, ILogger log)
{
    private static readonly ulong EmptyHash = ClipboardUtils.ClipboardHash(ClipboardSnapshot.Empty);

    private readonly EchoFallback _echo = new(sync, log);
    private readonly ClipboardChanges _changes = ClipboardChanges.Of(sync);
    private readonly Lock _lock = new();
    // one clipboard write at a time, its copied-since check with it: the relay delivers answers one at a time, but a held
    // answer's deadline lands it from a timer or a hash read. Never taken on the input actor, which writes nothing, and
    // never under _lock
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Dictionary<string, HostPulls> _hosts = new(StringComparer.OrdinalIgnoreCase);
    // the first pull to each host whose answer the master did not take (failed, replaced, or forgotten unanswered), so the
    // next pull to it asks a resend from there; outlives ForgetPull, dropped once a changed answer from that host is
    // written, a changed answer to a newer pull to another supersedes it, or the user copied here since. Under _lock
    private readonly Dictionary<string, Untaken> _untaken = new(StringComparer.OrdinalIgnoreCase);
    private int _ids;
    // the clipboard as last read or applied, to pull against when it cannot be read. under _lock, as are the rest
    private ClipboardBaseline _known = ClipboardBaseline.Of(ClipboardSnapshot.Empty);
    // the hash of Hydra's own last write here, a clear or an answer applied, which no user copy is
    private ulong _wrote = EmptyHash;
    // whether the last clipboard read or applied here was a password manager's copy, so an empty one since was cleared at
    // its source, which the slaves are told on entry
    private bool _concealed;
    // bumped by every apply or clear, so a read begun before one never writes its older state over it
    private int _version;

    // for tests: what to offer a slave on entering it; null for nothing, as for a clipboard that could not be read. An
    // empty one after a concealed copy says so, for a slave still holding Hydra's write of that copy to clear it
    public ClipboardHashMessage? Hash() => Hash(null);

    // as Hash(), offered to host: carrying its ResendFrom as of the same clipboard, both taken while no apply or clear
    // came between them (read again, twice more at most), so an answer from host taken meanwhile never leaves it the
    // older hash without the resend that answer ended
    public ClipboardHashMessage? Hash(string? host)
    {
        ClipboardHashMessage? hash = null;
        for (var i = 0; i < 3; i++)
        {
            var read = ReadAt("hash");
            var now = host == null ? null : _changes.Now();
            lock (_lock)
            {
                var settled = read.Version == _version;
                if (read.Clip is not { } clip) hash = null;
                else if (!clip.HasClipboard && _concealed) hash = ClipboardWire.ToHash(clip) with { Cleared = true };
                else hash = clip.IsEmpty ? null : ClipboardWire.ToHash(clip);
                if (hash != null && host != null) hash = hash with { ResendFrom = ResendFromLocked(host, now) };
                if (settled) break;
            }
        }
        return hash;
    }

    // the push a slave asked for; null for nothing
    public ClipboardPushMessage? Push() => Read("push") is { IsEmpty: false } clip ? ClipboardWire.ToPush(clip) : null;

    // empties the clipboard, which stays Hydra's own write, so a read finds nothing rather than the last fallback; whether
    // it did. cleared: a password manager cleared the concealed copy it held, which the slaves are told on entry
    public bool Empty(bool cleared = false)
    {
        var emptied = _changes.Write(_echo.Empty);
        lock (_lock)
        {
            _version++;
            _known = ClipboardBaseline.Of(ClipboardSnapshot.Empty);
            _concealed = cleared;
            if (emptied) _wrote = EmptyHash;
        }
        return emptied;
    }

    // a pull answer from host began streaming: copied files are kept, else the clipboard is emptied
    public BeginDecision Begun(string host) => _echo.EmptyUnlessFiles("stream", host, () => Empty());

    // a Begin from host was rejected: it empties only Hydra's own write (EchoFallback.Rejected)
    public BeginDecision Rejected(string host) => _echo.Rejected(host, () => Begun(host));

    // for tests: runs as a write waits its turn, before the gate
    internal Action? WaitingToWrite { get; set; }

    // one clipboard write at a time (TakePullAnswer, LandHeld, ForgetPull)
    private void EnterWrite()
    {
        WaitingToWrite?.Invoke();
        _writeGate.Wait();
    }

    // for tests: whether this thread holds the state's lock
    internal bool Locked => _lock.IsHeldByCurrentThread;

    // for tests: a pull with pending given as it stands
    public ClipboardPullMessage Pull(string host, ulong? pending = null) => Pull(host, _ => pending);

    // a pull to host against the last known clipboard, without reading it; while a stream from host is accepted it keeps
    // it outstanding, and carries the stream's hash (pendingFrom, asked under _lock) once the receiver has taken the
    // Begin. It asks a resend (ResendFrom) when an answer from host was not taken. The stream is never ended here, only by
    // its own end. pendingFrom must never call back here
    public ClipboardPullMessage Pull(string host, Func<string, ulong?> pendingFrom)
    {
        ClipboardBaseline against;
        ulong? pending;
        int id;
        int? resendFrom;
        // read before the lock: on X11 the counter can ask the X server
        var now = _changes.Now();
        lock (_lock)
        {
            against = _known;
            var pulls = For(host);
            // the answer to the pull this replaces is ignored if it comes, so never taken
            if (pulls.Latest is { } replaced) NotTaken(replaced, now);
            id = ++_ids;
            pulls.Latest = new PendingPull(new OutstandingPull(host, id), against, now);
            // an answer held for the pull this replaces now waits on this one, which carries its content as pending
            if (pulls.Held is { } held)
            {
                pulls.Held = held with { For = id };
                pending = held.ContentHash;
            }
            else pending = pulls.Streaming != null ? pendingFrom(host) : null;
            resendFrom = Standing(host, now)?.From;
        }
        return ClipboardWire.ToPull(pending is { } hash ? against with { Hash = hash } : against, id, resendFrom);
    }

    // whether host has a newer pull out than pullId
    private bool IsOlderThanLatest(string host, int pullId)
    {
        lock (_lock)
            return _hosts.TryGetValue(host, out var pulls) && pulls.Latest is { } latest && latest.Ref.Id != pullId;
    }

    // what host's entry hash carries as ResendFrom: the first pull to it whose answer was not taken, else the oldest still
    // out to it, whose answer the master may yet not take; never one a copy made on the master since outranks (its
    // answer is ignored when it lands, and the slave must take the master's newer clipboard); null for none
    public int? ResendFrom(string host)
    {
        var now = _changes.Now();
        lock (_lock)
            return ResendFromLocked(host, now);
    }

    // under _lock: ResendFrom, with now the change counter read before the lock
    private int? ResendFromLocked(string host, long? now)
    {
        if (Standing(host, now) is { } untaken) return untaken.From;
        if (!_hosts.TryGetValue(host, out var pulls)) return null;
        return pulls.Pending.Where(p => !CopiedSince(p.SentAt, p.Against.Hash, now)).Select(p => (int?)p.Ref.Id).Min();
    }

    // under _lock: whether the user copied on the master since a pull went out at sentAt against hash: by the counter,
    // now, read before the lock; without one, the known clipboard is neither that nor Hydra's own write
    private bool CopiedSince(long? sentAt, ulong? against, long? now) =>
        _changes.CopiedSince(sentAt, now) ?? (_known.Hash != against && _known.Hash != _wrote);

    // whether pull is the one whose answer streams in to its host
    public bool IsStreaming(OutstandingPull pull)
    {
        lock (_lock)
            return _hosts.TryGetValue(pull.Host, out var pulls) && pulls.Streaming?.Ref.Id == pull.Id;
    }

    // the pulls whose answers stream in, oldest first
    public IReadOnlyList<OutstandingPull> StreamingIn
    {
        get
        {
            lock (_lock)
                return [.. _hosts.Values.Select(p => p.Streaming?.Ref).OfType<OutstandingPull>().OrderBy(p => p.Id)];
        }
    }

    // the newer pulls a completed answer is held for, which their answers (or their being waited out) land; oldest first
    public IReadOnlyList<OutstandingPull> HeldFor
    {
        get
        {
            lock (_lock)
                return [.. _hosts.Values.Where(p => p.Held != null).Select(p => p.Latest?.Ref).OfType<OutstandingPull>().OrderBy(p => p.Id)];
        }
    }

    // the unanswered pulls, those whose answers are streaming in among them, oldest first
    public IReadOnlyList<OutstandingPull> Outstanding
    {
        get
        {
            lock (_lock)
                return [.. Unanswered()];
        }
    }

    // the pull a Begin from host answers, the one it names, accepted before the receiver takes the Begin: that pull is
    // noted as the one streaming in, off the latest, so a pull made meanwhile keeps it outstanding; it carries the stream's
    // hash once the receiver has taken the Begin. Whatever ends the stream ends it here too (StreamEnded, TakePullAnswer,
    // RefuseStream, ForgetPull, a supersede). Null for none, as for a pull superseded or replaced since
    public OutstandingPull? StreamFor(string host, int pullId)
    {
        lock (_lock)
        {
            if (Find(host, pullId) is not { } found) return null;
            var pulls = For(host);
            // a newer copy streams in, so an older answer held for this pull is no longer wanted
            if (pulls.Latest == found) pulls.ForgetLatest();
            pulls.Streaming = found;
            return found.Ref;
        }
    }

    // the answer to pull began streaming, so it is a changed clipboard: every older pull is forgotten, and returned,
    // oldest first, since its answer is no longer wanted; a newer pull to host stays, answered beside the stream. Its
    // Begin's accept (StreamFor) already noted it streaming in. resends: the Begin's Resends, which ranks it
    public IReadOnlyList<OutstandingPull> Streaming(OutstandingPull pull, int? resends = null)
    {
        PendingPull? found;
        lock (_lock)
            found = Find(pull.Host, pull.Id);
        if (found == null) return [];
        var superseded = Supersede(pull, resends);
        lock (_lock)
        {
            // one never accepted is noted now, off the latest
            if (_hosts.TryGetValue(pull.Host, out var pulls) && pulls.Latest == found)
            {
                pulls.ForgetLatest();
                pulls.Streaming = found;
            }
        }
        return superseded;
    }

    // a Begin answering pull, refused when the user copied on the master since it went out: that copy is newer, so the
    // pull is resolved unanswered and the stream must not empty the clipboard. Whether it was refused
    public bool RefuseStream(OutstandingPull pull)
    {
        PendingPull? found;
        lock (_lock)
            found = Find(pull.Host, pull.Id);
        if (found == null || !CopiedSince(found)) return false;
        lock (_lock)
        {
            var pulls = For(pull.Host);
            if (pulls.Latest == found) pulls.ForgetLatest();
            if (pulls.Streaming == found) pulls.Streaming = null;
        }
        if (log.IsEnabled(LogLevel.Debug))
            log.LogDebug("Clipboard stream from {Host} refused: copied on the master since pull {PullId}", pull.Host, pull.Id);
        return true;
    }

    // the stream from host ended unapplied (aborted, failed or abandoned): the pull it answered is no longer outstanding;
    // with pullId (the one its Begin named) only while that pull is still the one streaming, not a newer stream's. failed:
    // the master did not take that answer, so the next pull to host asks a resend
    public void StreamEnded(string host, int? pullId = null, bool failed = false)
    {
        var now = _changes.Now();
        lock (_lock)
        {
            if (!_hosts.TryGetValue(host, out var pulls) || pulls.Streaming is not { } streaming) return;
            if (pullId is { } id && streaming.Ref.Id != id) return;
            if (failed) NotTaken(streaming, now);
            pulls.Streaming = null;
        }
    }

    // under _lock: the answer to pull was not taken, so host's next pull asks a resend from the first such pull still
    // standing, unless a copy made on the master since it went out outranks that answer; now: the change counter, read
    // before the lock
    private void NotTaken(PendingPull pull, long? now)
    {
        var host = pull.Ref.Host;
        if (CopiedSince(pull.SentAt, pull.Against.Hash, now)) return;
        if (!(Standing(host, now)?.From < pull.Ref.Id)) _untaken[host] = new Untaken(pull.Ref.Id, pull.SentAt, _known.Hash);
        if (log.IsEnabled(LogLevel.Debug))
            log.LogDebug("Clipboard pull {PullId} to {Host}: its answer was not taken, the next pull asks a resend", pull.Ref.Id, host);
    }

    // under _lock: host's answer not taken, while it stands: the user copied nothing here since its pull went out
    // (without a counter: the known clipboard is as it was when recorded, or Hydra's own write). A newer answer changing
    // the clipboard drops it in Supersede; Hydra's own writes and a selection alone never do. now: the change counter,
    // read before the lock
    private Untaken? Standing(string host, long? now)
    {
        if (!_untaken.TryGetValue(host, out var untaken)) return null;
        if (!CopiedSince(untaken.SentAt, untaken.Known, now)) return untaken;
        _untaken.Remove(host);
        return null;
    }

    // forgets the outstanding pulls to host (every pull when null), so a later answer to them is ignored. An answer held
    // for a newer pull answered its own, so it lands unless the user copied here since; what each one landed did, its
    // Answer null when it could not be written
    public IReadOnlyList<HeldLanding> ForgetPull(string? host = null)
    {
        // the held answers leave state and are written within one pass of the gate, so no landing races them
        EnterWrite();
        try
        {
            return Forget(host);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // under _writeGate: ForgetPull
    private List<HeldLanding> Forget(string? host)
    {
        List<string> forgotten;
        List<HeldAnswer> held;
        List<PendingPull> unanswered;
        lock (_lock)
        {
            List<HostPulls> removed;
            if (host == null)
            {
                forgotten = [.. _hosts.Where(h => h.Value.Pending.Any()).Select(h => h.Key)];
                removed = [.. _hosts.Values];
                _hosts.Clear();
            }
            else
            {
                removed = _hosts.Remove(host, out var pulls) ? [pulls] : [];
                forgotten = removed.Any(p => p.Pending.Any()) ? [host] : [];
            }
            unanswered = [.. removed.SelectMany(p => p.Pending)];
            held = [.. removed.Select(p => p.Held).OfType<HeldAnswer>()];
        }
        if (log.IsEnabled(LogLevel.Debug))
            foreach (var h in forgotten)
                log.LogDebug("Clipboard pull to {Host} forgotten", h);
        List<HeldLanding> landed = [.. held.Select(Land).OfType<HeldLanding>()];
        // an answer still to come to a pull forgotten, the one streaming in among them, is never taken, nor outranked by
        // the older one held for it; the first of them is asked from
        var now = _changes.Now();
        lock (_lock)
            foreach (var pull in unanswered)
                NotTaken(pull, now);
        return landed;
    }

    // the newer pull an answer is held for was waited out unanswered: the held answer lands now, as when that pull is
    // forgotten (null when none was held for it or one since, a supersede dropped it first, or a copy made here since
    // outranks it). The pull stays out: its slow answer, a changed one newer than the held copy, still lands over it, an
    // unchanged one taking only its selection. One held on for a pull made since lands too
    public HeldLanding? LandHeld(OutstandingPull pull)
    {
        EnterWrite();
        try
        {
            HeldAnswer? held;
            lock (_lock)
            {
                // still held: kept in state until written, so a supersede that ran first has dropped it
                if (!_hosts.TryGetValue(pull.Host, out var pulls) || pulls.Held is not { } kept || kept.For < pull.Id) return null;
                held = kept;
                pulls.Held = null;
            }
            if (log.IsEnabled(LogLevel.Debug))
                log.LogDebug("Clipboard pull {PullId} to {Host} waited out: the answer held for it lands", pull.Id, pull.Host);
            return Land(held);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // a held answer whose newer pull will never be answered; null when a copy made here since outranks it
    private HeldLanding? Land(HeldAnswer held)
    {
        var host = held.Pull.Ref.Host;
        if (held.Answer.Unchanged != true && CopiedSince(held.Pull))
        {
            if (log.IsEnabled(LogLevel.Debug))
                log.LogDebug("Clipboard pull response from {Host} held, dropped: copied on the master since pull {PullId}", host, held.Pull.Ref.Id);
            return null;
        }
        var answer = Apply(host, held.Pull, held.Answer, []);
        return new HeldLanding(held.Pull.Ref, answer.Applied != null || held.Answer.Unchanged == true ? answer : null);
    }

    // a slave's answer to a pull: whether it was taken, what it applied, and the older pulls it superseded
    public PullAnswer TakePullAnswer(string host, ClipboardPullResponseMessage answer)
    {
        EnterWrite();
        try
        {
            return Take(host, answer);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // under _writeGate: TakePullAnswer
    private PullAnswer Take(string host, ClipboardPullResponseMessage answer)
    {
        PendingPull? pull;
        PendingPull? replaced = null;
        HeldAnswer? held = null;
        int? heldFor = null;
        // a changed answer to an older pull may be held, carrying its content's hash, hashed before the lock (a big one
        // takes milliseconds) so the hold is decided and set at once
        ulong? content = null;
        if (answer.Unchanged != true && IsOlderThanLatest(host, answer.PullId))
            content = ClipboardUtils.ClipboardHash(ClipboardWire.From(answer, log, $"pull response from {host}"));
        var now = _changes.Now();
        lock (_lock)
        {
            var pulls = For(host);
            pull = pulls.Find(answer.PullId);
            if (pull != null && pulls.Latest == pull)
            {
                // a changed answer to the newer pull replaced the stream answering an older one, or the answer held
                if (answer.Unchanged != true)
                {
                    replaced = pulls.Streaming;
                    pulls.Streaming = null;
                }
                // a held answer lands only beside the answer to the pull it was held for; one held for another never
                // lands, so was never taken
                if (pulls.Held is { } kept)
                {
                    if (kept.For == pull.Ref.Id) held = kept;
                    else NotTaken(kept.Pull, now);
                }
                pulls.ForgetLatest();
            }
            else if (pull != null)
            {
                pulls.Streaming = null;
                // a stream completing after a newer pull to host went out waits for that pull's answer, which may be newer;
                // one made since the check above carries no content hash, so its slave answers it in full
                if (pulls.Latest is { } latest)
                {
                    heldFor = latest.Ref.Id;
                    pulls.Held = new HeldAnswer(pull, answer, latest.Ref.Id, answer.Unchanged == true ? null : content);
                }
            }
        }
        if (pull == null)
        {
            if (log.IsEnabled(LogLevel.Debug))
                log.LogDebug("Clipboard pull response from {Host} ignored (not the answer to an outstanding pull to it)", host);
            return PullAnswer.Ignored;
        }
        if (log.IsEnabled(LogLevel.Debug))
            log.LogDebug("Clipboard pull response from {Host}: {Answer}", host, ClipboardWire.Describe(answer));
        if (heldFor is { } waitsOn)
        {
            if (log.IsEnabled(LogLevel.Debug))
                log.LogDebug("Clipboard pull response from {Host} held until pull {PullId} is answered", host, waitsOn);
            return new PullAnswer(true, null, [], HeldFor: new OutstandingPull(host, waitsOn));
        }
        List<OutstandingPull> gone = replaced == null ? [] : [replaced.Ref];
        if (answer.Unchanged == true && held != null)
        {
            // the newer pull found nothing newer than the held stream, which now lands as its own pull's answer, with
            // the newer selection
            pull = held.Pull;
            answer = held.Answer with { PrimaryText = answer.PrimaryText, PrimaryIsText = answer.PrimaryIsText };
        }
        // a selection alone never overwrites a copy, so only a changed clipboard loses to one made here since
        if (answer.Unchanged != true && CopiedSince(pull))
        {
            if (log.IsEnabled(LogLevel.Debug))
                log.LogDebug("Clipboard pull response from {Host} ignored: copied on the master since pull {PullId}", host, answer.PullId);
            return new PullAnswer(true, null, gone);
        }
        return Apply(host, pull, answer, gone);
    }

    // writes host's answer to pull, superseding the older pulls when it changed the clipboard; gone: those it already did
    private PullAnswer Apply(string host, PendingPull pull, ClipboardPullResponseMessage answer, List<OutstandingPull> gone)
    {
        var cleared = answer.Cleared == true && ClearConcealed(host);
        var write = new ClipboardWrite();
        var applied = _changes.Write(() => ClipboardWire.TakePullAnswer(sync, answer, pull.Against, log, host, write), _ => write.Replaced);
        // the placeholder in place of a concealed copy whose markers were refused is an empty clipboard, Hydra's own
        var refused = write.MarkersRefused;
        if (refused) _echo.Forget();
        else _echo.Remember(applied);
        var wrote = applied is { HasClipboard: true } && !cleared;
        var now = _changes.Now();
        lock (_lock)
        {
            // an answer from host not taken still stands beside this one when this answers an older pull (a held one)
            var standing = Standing(host, now);
            if (refused)
            {
                _version++;
                _known = ClipboardBaseline.Of(applied ?? ClipboardSnapshot.Empty);
                _concealed = false;
                _wrote = EmptyHash;
            }
            else if (applied != null)
            {
                _version++;
                _known = applied.IsSelectionOnly ? _known with { PrimaryText = applied.PrimaryText } : ClipboardBaseline.Of(applied);
            }
            if (wrote)
            {
                _concealed = applied!.Concealed;
                _wrote = ClipboardUtils.ClipboardHash(applied);
            }
            // a changed answer written is taken, and with it every answer from host before it; one that could not be is
            // not, so the next pull asks again. What this host sent stands on, as of the clipboard it left here
            if (write.Failed && !refused && answer.Unchanged != true) NotTaken(pull, now);
            else if (standing != null && (answer.Unchanged == true || standing.From > pull.Ref.Id))
                _untaken[host] = standing with { Known = _known.Hash };
            else _untaken.Remove(host);
        }
        if (refused)
        {
            log.LogWarning("Clipboard pull response from {Host}: the clipboard refused its concealed markers, so it was not written; the clipboard stays empty",
                host);
            return new PullAnswer(true, ClipboardSnapshot.Placeholder, [.. gone, .. Supersede(pull.Ref, answer.Resends)], Failed: true);
        }
        if (write.Failed)
            log.LogWarning("Clipboard pull response from {Host}: it could not be written; the clipboard stays as it was", host);
        // the clear is the newer state: it supersedes the older pulls and aborts the pushes of the old copy, as a copy does
        if (cleared) return new PullAnswer(true, ClipboardSnapshot.Placeholder, [.. gone, .. Supersede(pull.Ref, answer.Resends)]);
        // a changed answer that could not be written is still the newest copy, asked again: no older one lands over it
        if (write.Failed && answer.Unchanged != true)
            return new PullAnswer(true, applied, [.. gone, .. Supersede(pull.Ref, answer.Resends)], Failed: true);
        if (!wrote) return new PullAnswer(true, applied, gone, write.Failed);
        return new PullAnswer(true, applied, [.. gone, .. Supersede(pull.Ref, answer.Resends)]);
    }

    // host's concealed copy was cleared at its source: so is the master's clipboard, while it holds a concealed copy
    private bool ClearConcealed(string host)
    {
        if (Read("pull response") is not { HasClipboard: true, Concealed: true }) return false;
        if (log.IsEnabled(LogLevel.Debug))
            log.LogDebug("Clipboard pull response from {Host}: its concealed copy was cleared, clearing the master's", host);
        // the slaves given that copy are told on entry
        var cleared = Empty(cleared: true);
        if (!cleared) log.LogWarning("Clipboard pull response from {Host}: the master's concealed copy could not be emptied", host);
        return cleared;
    }

    // whether the user copied here since pull went out: the change counter moved past it and past Hydra's own last write;
    // where there is no counter, the clipboard is neither what pull went against nor that write
    private bool CopiedSince(PendingPull pull)
    {
        var counted = _changes.CopiedSince(pull.SentAt);
        if (counted == false) return false;
        // a clipboard that cannot be read is never taken for the user's copy
        if (Read("pull response") is not { } read) return false;
        if (counted == true) return true;
        var now = ClipboardUtils.ClipboardHash(read);
        lock (_lock)
            return now != pull.Against.Hash && now != _wrote;
    }

    // under _lock: the outstanding pull to host with pullId, the latest or the one whose answer streams in
    private PendingPull? Find(string host, int pullId) => _hosts.TryGetValue(host, out var pulls) ? pulls.Find(pullId) : null;

    // under _lock: host's pulls, added when it has none
    private HostPulls For(string host)
    {
        if (!_hosts.TryGetValue(host, out var pulls)) _hosts[host] = pulls = new HostPulls();
        return pulls;
    }

    // under _lock: the unanswered pulls to every host, those streaming in among them, oldest first
    private IEnumerable<OutstandingPull> Unanswered() => _hosts.Values.SelectMany(h => h.Pending).Select(p => p.Ref).Distinct().OrderBy(p => p.Id);

    // the copy answering newer supersedes every older pull; resends: the older pull a resent copy first answered, which
    // ranks it against other hosts, so it never outranks a copy from elsewhere newer than that one
    private List<OutstandingPull> Supersede(OutstandingPull newer, int? resends = null)
    {
        var rank = Math.Min(resends ?? newer.Id, newer.Id);
        bool Older(string host, int id) => id < (host.EqualsIgnoreCase(newer.Host) ? newer.Id : rank);
        List<OutstandingPull> superseded;
        lock (_lock)
        {
            superseded = [.. Unanswered().Where(p => Older(p.Host, p.Id))];
            // a newer copy outranks every other host's answer not taken to an older pull
            foreach (var other in _untaken.Where(u => !u.Key.EqualsIgnoreCase(newer.Host) && u.Value.From < rank).Select(u => u.Key).ToList())
                _untaken.Remove(other);
            foreach (var entry in _hosts)
            {
                var pulls = entry.Value;
                // an answer held for a superseded pull is older than the newer copy
                if (pulls.Latest is { } latest && Older(entry.Key, latest.Ref.Id)) pulls.ForgetLatest();
                if (pulls.Held is { } held && Older(entry.Key, held.Pull.Ref.Id)) pulls.Held = null;
                if (pulls.Streaming is { } streaming && Older(entry.Key, streaming.Ref.Id)) pulls.Streaming = null;
            }
        }
        if (log.IsEnabled(LogLevel.Debug))
            foreach (var older in superseded)
                log.LogDebug("Clipboard pull {PullId} to {Host} superseded by the newer copy from {From}", older.Id, older.Host, newer.Host);
        return superseded;
    }

    // the clipboard, kept as the one known and noting whether it holds a concealed copy, unless an apply or clear came
    // since the read began, which is newer than what it found; null when it could not be read
    private ClipboardSnapshot? Read(string context) => ReadAt(context).Clip;

    // as Read, with the version it began at: one an apply or clear has moved past since is older than the clipboard now
    private VersionedRead ReadAt(string context)
    {
        int version;
        lock (_lock)
            version = _version;
        if (_echo.Read(context) is not { } clip) return new VersionedRead(null, version);
        lock (_lock)
        {
            if (version != _version) return new VersionedRead(clip, version);
            _known = ClipboardBaseline.Of(clip);
            if (clip.HasClipboard) _concealed = clip.Concealed;
        }
        return new VersionedRead(clip, version);
    }

    private sealed record VersionedRead(ClipboardSnapshot? Clip, int Version);

    // sentAt: the change counter when it went out, null where the platform keeps none
    private sealed record PendingPull(OutstandingPull Ref, ClipboardBaseline Against, long? SentAt);

    // the first pull to a host whose answer was not taken: its id, the change counter when it went out, and the known
    // clipboard's hash as it stands
    private sealed record Untaken(int From, long? SentAt, ulong? Known);

    // for: the id of the newer pull whose answer it waits on; contentHash: the clipboard it carries, null for an unchanged one
    private sealed record HeldAnswer(PendingPull Pull, ClipboardPullResponseMessage Answer, int For, ulong? ContentHash);

    // under _lock: a host's latest pull, the pull its answer streams in for (which may be the latest), and a streamed
    // answer that completed after a newer pull went out, applied once that pull finds nothing newer
    private sealed class HostPulls
    {
        public PendingPull? Latest { get; set; }
        public PendingPull? Streaming { get; set; }
        public HeldAnswer? Held { get; set; }

        public IEnumerable<PendingPull> Pending => new[] { Latest, Streaming }.OfType<PendingPull>();

        public PendingPull? Find(int pullId) => Latest?.Ref.Id == pullId ? Latest : Streaming?.Ref.Id == pullId ? Streaming : null;

        // the latest pull is answered or gone, and with it any answer held for it
        public void ForgetLatest()
        {
            Latest = null;
            Held = null;
        }
    }
}

// an unanswered pull: the host it went to, and its id
public sealed record OutstandingPull(string Host, int Id)
{
    public bool Is(OutstandingPull other) => other.Id == Id && other.Host.EqualsIgnoreCase(Host);
}

// what became of a pull answer: whether it answered the latest pull to its host, what it applied, the older pulls its
// newer copy superseded, whether its write failed, to be warned about and shown where the cursor is, and the newer pull
// it is held for, whose answer lands it
public sealed record PullAnswer(bool Taken, ClipboardSnapshot? Applied, IReadOnlyList<OutstandingPull> Superseded, bool Failed = false,
    OutstandingPull? HeldFor = null)
{
    public static readonly PullAnswer Ignored = new(false, null, []);
}

// a held answer landed as its host's pulls were forgotten: the pull it answered, and what became of it, null when it
// could not be written
public sealed record HeldLanding(OutstandingPull Pull, PullAnswer? Answer);
