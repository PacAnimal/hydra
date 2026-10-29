using Hydra.Platform;
using Microsoft.Extensions.Logging;

namespace Hydra.Relay;

// the slave's side of the clipboard handshake, and what it keeps between the master's messages; pendingFrom is the
// content hash of the stream pending from a host, which counts as already applied
public sealed class ClipboardSlave(IClipboardSync sync, ILogger log, Func<string, ulong?> pendingFrom)
{
    private readonly EchoFallback _echo = new(sync, log);
    private readonly ClipboardChanges _changes = ClipboardChanges.Of(sync);
    private readonly Dictionary<string, PerMaster> _masters = new(StringComparer.OrdinalIgnoreCase);
    // whether Hydra has written the clipboard here, so one still its own write holds nothing new
    private bool _wrote;
    // a clock over what was seen here: the last time the clipboard was seen as Hydra's own write, and per master the start
    // of its visit, so a copy since then is the user's
    private long _clock;
    private long _oursAt = -1;

    // the master's cursor entered here: a copy made since is the user's
    public void Entered(string host)
    {
        NewVisit(host);
        Ours();
    }

    // whether the clipboard is Hydra's own write, which counts as seen so
    private bool Ours()
    {
        if (!sync.CanUseEchoFallback()) return false;
        _oursAt = ++_clock;
        return true;
    }

    private void NewVisit(string host)
    {
        var master = For(host);
        master.VisitFrom = ++_clock;
        master.VisitAt = _changes.Now();
    }

    // whether the user copied here since host's visit began: by the change counter, else by the clock of Hydra's writes,
    // the clipboard having been Hydra's own write during the visit and no longer so
    private bool CopiedThisVisit(string host, bool ours)
    {
        var master = For(host);
        return _changes.CopiedSince(master.VisitAt) ?? (!ours && _oursAt > master.VisitFrom);
    }

    // the master's hash, against this host's clipboard: what it needs pushed, emptying the clipboard while one is coming
    public PushNeed OnHash(ClipboardHashMessage hash, string host)
    {
        var master = For(host);
        master.AwaitingPush = null;
        master.EmptiedFor = null;
        var ours = Ours();
        if (hash.Cleared == true) ClearConcealed(host, ours);
        if (hash.Pending == true)
        {
            // a copy the master never took is newer than the clipboard on its way, so it is kept for the resend
            if (!UntakenAnswer(hash, host)) EmptyForPending(host);
            master.SelectionPushFor = null;
            ClipboardWire.NeedsPush(hash with { Hash = null, PrimaryHash = null }, sync, ClipboardBaseline.SelectionOf(sync), log, host);
            return PushNeed.None;
        }
        // the clipboard no longer ours since the visit began: the user copied here since, and that copy wins, once; the
        // entry hash too, which may come late (deferred)
        if (CopiedThisVisit(host, ours))
        {
            NewVisit(host);
            return KeepUsersCopy(hash, host);
        }
        // the master never took the full answer that synced this clipboard (or has yet to), so its hash is older: the
        // copy is kept for the next pull to resend, as the user's copy is
        if (UntakenAnswer(hash, host)) return KeepUsersCopy(hash, host);
        var local = hash.Hash == null ? ClipboardBaseline.SelectionOf(sync) : _echo.Baseline("hash check");
        // a clipboard that could not be read asks for nothing this time, rather than have the master's replace it
        if (local == null)
        {
            master.SelectionPushFor = null;
            return PushNeed.None;
        }
        var pending = hash.Hash != null && pendingFrom(host) == hash.Hash;
        if (pending) local = local with { Hash = hash.Hash };
        var need = ClipboardWire.NeedsPush(hash, sync, local, log, host);
        // against a pending stream the clipboard here is empty, so a push that follows is applied whole
        master.SelectionPushFor = need == PushNeed.Selection && !pending ? local : null;
        if (pending) RememberSelectionSince(hash, local, need, master);
        // a paste right after crossing never gets the old clipboard
        if (need == PushNeed.Clipboard && Begun(host, hash.Hash) is var decision and not BeginDecision.Keep)
        {
            master.AwaitingPush = new AwaitedPush(hash.Visit);
            // where the counter stood, even after a failed clear, so a copy made since outranks the push; without a counter
            // only a clear that took says the clipboard was Hydra's then
            if (decision == BeginDecision.Cleared || _changes.Now() != null) master.EmptiedFor = new ChangePoint(_changes.Now());
        }
        if (!log.IsEnabled(LogLevel.Debug)) return need;
        if (need == PushNeed.None)
            log.LogDebug("Clipboard hash from {Host}: matches", host);
        else
            log.LogDebug("Clipboard hash from {Host}: {Need} differs, requesting push", host, need);
        return need;
    }

    // streamed is a push whose stream just completed, which keeps a selection taken since its Begin; whether it failed to
    // be written (warned about here), to be shown
    public bool OnPush(ClipboardPushMessage push, string host, bool streamed = false)
    {
        var master = For(host);
        master.AwaitingPush = null;
        master.EmptiedFor = null;
        var since = master.SelectionSince;
        master.SelectionSince = null;
        if (since != null && streamed)
        {
            if (log.IsEnabled(LogLevel.Debug))
                log.LogDebug("Clipboard push from {Host}: keeping the selection taken since its stream began", host);
            push = push with { PrimaryText = since.PrimaryText, PrimaryIsText = since.PrimaryIsText };
        }
        if (log.IsEnabled(LogLevel.Debug))
            log.LogDebug("Clipboard push from {Host}: {Push}", host, ClipboardWire.Describe(push));
        var selectionFor = master.SelectionPushFor;
        master.SelectionPushFor = null;
        // host's clipboard replaces what this host last answered it with
        master.AnsweredConcealed = false;
        var write = new ClipboardWrite();
        var applied = _changes.Write(() => ClipboardWire.ApplyPush(sync, push, selectionFor, log, host, write), _ => write.Replaced);
        if (write.MarkersRefused)
        {
            // the placeholder in place of a concealed copy whose markers were refused: an empty clipboard, Hydra's own
            _echo.Forget();
            Wrote();
            master.SyncedWith = null;
            master.SyncedBy = null;
            master.ConcealedFrom = null;
            log.LogWarning("Clipboard push from {Host}: the clipboard refused its concealed markers, so it was not written; the clipboard stays empty", host);
            return true;
        }
        _echo.Remember(applied);
        if (write.Failed) log.LogWarning("Clipboard push from {Host}: it could not be written", host);
        if (applied is not { IsSelectionOnly: false }) return write.Failed;
        Wrote();
        master.SyncedWith = ClipboardUtils.ClipboardHash(applied);
        master.SyncedBy = null;
        master.SyncedAt = _changes.Now();
        master.ConcealedFrom = applied.Concealed ? master.SyncedWith : null;
        return false;
    }

    // the master's concealed copy was cleared at its source: so is that copy here, while the clipboard is still Hydra's write
    // of it, never a copy made since
    private void ClearConcealed(string host, bool ours)
    {
        var master = For(host);
        var concealed = master.ConcealedFrom;
        master.ConcealedFrom = null;
        if (concealed == null || !ours) return;
        if (_echo.Read("cleared hash") is not { HasClipboard: true } local || ClipboardUtils.ClipboardHash(local) != concealed) return;
        if (log.IsEnabled(LogLevel.Debug))
            log.LogDebug("Clipboard hash from {Host}: its concealed copy was cleared, clearing it here", host);
        Empty();
    }

    // host's clipboard was superseded by another master's: what it asked for or sent beside its stream is void
    public void Superseded(string host)
    {
        var master = For(host);
        master.SelectionPushFor = null;
        master.SelectionSince = null;
        master.AwaitingPush = null;
        master.EmptiedFor = null;
    }

    // host's push (whole, or a stream's Begin) is coming in: whether the user copied here since its hash emptied the
    // clipboard for it, a copy newer than that push, which is then refused
    public bool CopiedSinceHash(string host)
    {
        var master = For(host);
        if (master.EmptiedFor is not { } emptied) return false;
        master.EmptiedFor = null;
        var ours = Ours();
        if (!(_changes.CopiedSince(emptied.Count) ?? !ours)) return false;
        master.AwaitingPush = null;
        master.SelectionPushFor = null;
        if (log.IsEnabled(LogLevel.Debug))
            log.LogDebug("Clipboard push from {Host}: copied here since its hash, keeping that copy", host);
        return true;
    }

    private void Wrote()
    {
        _wrote = true;
        _oursAt = ++_clock;
    }

    // a stream from host began: whatever selection came beside an older one is older than it
    public void StreamBegun(string host)
    {
        var master = For(host);
        master.SelectionSince = null;
        master.AwaitingPush = null;
    }

    // host has no push for the request its hash for visit made, or for a later visit (ids only grow): whether that
    // emptied the clipboard here, which then stays empty
    public bool NoPush(ClipboardPushNoneMessage none, string host)
    {
        var master = For(host);
        if (master.AwaitingPush is not { } awaited || !CoversVisit(none.Visit, awaited.Visit)) return false;
        master.AwaitingPush = null;
        return true;
    }

    // a visitless awaited push matches only a visitless none
    private static bool CoversVisit(int? none, int? awaited) => awaited == null ? none == null : none >= awaited;

    // the pushes the hashes of masters not in present (every master when null) emptied the clipboard for will never come,
    // each warned about (why): whether any was awaited, the clipboard then staying empty
    public bool ForgetAwaitedPushes(string why, IReadOnlySet<string>? present = null)
    {
        var any = false;
        foreach (var entry in _masters)
        {
            var master = entry.Value;
            if (master.AwaitingPush == null || present?.Contains(entry.Key) == true) continue;
            master.AwaitingPush = null;
            any = true;
            log.LogWarning("Clipboard push from {Host} will not come: {Why}; the clipboard stays empty", entry.Key, why);
        }
        return any;
    }

    // the master's selection beside the clipboard still streaming here is newer than the one that stream carries; one sent
    // as a hash that matched is the selection already here
    private static void RememberSelectionSince(ClipboardHashMessage hash, ClipboardBaseline local, PushNeed need, PerMaster master)
    {
        if (hash.PrimaryHash == null && (hash.PrimaryText != null || hash.PrimaryIsText == true))
            master.SelectionSince = new SelectionSince(hash.PrimaryText, hash.PrimaryIsText);
        else if (hash.PrimaryHash != null && need == PushNeed.None)
            master.SelectionSince = new SelectionSince(local.PrimaryText, null);
    }

    // empties the clipboard, which stays Hydra's own write, so a read finds nothing rather than the last push; whether it did.
    // no password manager cleared it, so no master is told its concealed copy was
    public bool Empty()
    {
        Wrote();
        foreach (var master in _masters.Values) master.AnsweredConcealed = false;
        return _changes.Write(_echo.Empty);
    }

    // a stream from host began, carrying contentHash: copied files are kept, as is a clipboard the master's already
    // matches when only the selection was asked for; else it is emptied
    public BeginDecision Begun(string host, ulong? contentHash) =>
        _echo.EmptyUnlessFiles("stream", host, Empty, () => OnlySelectionWanted(host, contentHash));

    // a Begin from host was rejected: it empties only Hydra's own write (EchoFallback.Rejected)
    public BeginDecision Rejected(string host) => _echo.Rejected(host, () => Begun(host, null));

    private bool OnlySelectionWanted(string host, ulong? contentHash)
    {
        if (contentHash == null || For(host).SelectionPushFor?.Hash != contentHash) return false;
        if (log.IsEnabled(LogLevel.Debug))
            log.LogDebug("Clipboard stream from {Host}: the clipboard already matches, only its selection is wanted", host);
        return true;
    }

    private void EmptyForPending(string host) => _echo.EmptyUnlessFiles("pending", host, () =>
    {
        if (log.IsEnabled(LogLevel.Debug))
            log.LogDebug("Clipboard pending from {Host}: emptying, waiting for the next hash", host);
        return Empty();
    });

    // whether the clipboard is still the one host's hash says it never took: synced by this host's full answer to a pull
    // at or after the hash's ResendFrom, and not copied since
    private bool UntakenAnswer(ClipboardHashMessage hash, string host)
    {
        var master = For(host);
        if (!(master.SyncedBy >= hash.ResendFrom) || _changes.CopiedSince(master.SyncedAt) == true) return false;
        return _echo.Baseline("hash check") is { } local && local.Hash == master.SyncedWith;
    }

    // the user's copy against the master's hash: the clipboard counts as matching, so only a selection of its own beside
    // it is taken (one repeating the master's text went with that clipboard)
    private PushNeed KeepUsersCopy(ClipboardHashMessage hash, string host)
    {
        var master = For(host);
        if (_echo.Baseline("hash check") is not { } read)
        {
            master.SelectionPushFor = null;
            return PushNeed.None;
        }
        var local = read with { Hash = hash.Hash };
        var need = ClipboardWire.NeedsPush(hash with { Hash = null, PrimaryIsText = null }, sync, local, log, host);
        master.SelectionPushFor = need == PushNeed.Selection ? local : null;
        if (log.IsEnabled(LogLevel.Debug))
            log.LogDebug("Clipboard hash from {Host}: copied here since the entry, keeping that copy ({Need})", host, need);
        return need;
    }

    // the answer to the master's pull; unchanged, with nothing to tell, when the clipboard could not be read, is still
    // Hydra's own write (only the user's copy is new here), or is what was last synced with host and not copied since
    // (unless the pull asks a resend from the pull whose answer synced it, or an older one: the master never took it), a
    // stream pending from it counting as applied only while Hydra's own write
    public ClipboardPullResponseMessage OnPull(ClipboardPullMessage pull, string host)
    {
        var master = For(host);
        var local = _echo.Read("pull response");
        var ours = Ours();
        var pending = ours ? pendingFrom(host) : null;
        var localHash = local == null ? (ulong?)null : ClipboardUtils.ClipboardHash(local);
        // what was last synced with host is nothing new to it, whoever wrote it, unless copied again since
        var resend = master.SyncedBy >= pull.ResendFrom;
        var stillSynced = localHash != null && master.SyncedWith == localHash && _changes.CopiedSince(master.SyncedAt) != true;
        var synced = !resend && stillSynced;
        var answer = local == null
            ? new ClipboardPullResponseMessage(pull.PullId, null, Unchanged: true)
            : ClipboardWire.AnswerPull(local, pull, (ours && _wrote) || synced ? pull.MasterHash : pending == pull.MasterHash ? pending : null);
        if (local != null && answer.Unchanged != true) answer = TellCleared(answer, local, master);
        // the copy the master never took, sent again: ranked as the answer to the pull that first carried it, so it never
        // outranks a copy from elsewhere newer than that
        var resent = resend && stillSynced && answer.Unchanged != true;
        if (resent) answer = answer with { Resends = master.SyncedFirst };
        if (localHash is { } sent && (answer.Unchanged != true || pull.MasterHash == sent))
        {
            // the full answer that synced it, which the master may yet not take; an unchanged one keeps whichever did
            if (answer.Unchanged != true)
            {
                if (!resent) master.SyncedFirst = pull.PullId;
                master.SyncedBy = pull.PullId;
            }
            else if (master.SyncedWith != sent) master.SyncedBy = null;
            master.SyncedWith = sent;
            master.SyncedAt = _changes.Now();
        }
        // the master has the user's copy now, so a later offer is no older than it
        if (answer.Unchanged != true && !ours) NewVisit(host);
        if (log.IsEnabled(LogLevel.Debug))
            log.LogDebug("Clipboard pull to {Host}: {Answer}", host, ClipboardWire.Describe(answer));
        return answer;
    }

    // a full answer: an empty clipboard where the last one this host answered master with was concealed was cleared here
    // since (its password manager), which master is told once, so its copy of it is cleared too
    private static ClipboardPullResponseMessage TellCleared(ClipboardPullResponseMessage answer, ClipboardSnapshot local, PerMaster master)
    {
        var concealed = master.AnsweredConcealed;
        master.AnsweredConcealed = local is { HasClipboard: true, Concealed: true };
        return !local.HasClipboard && concealed ? answer with { Cleared = true } : answer;
    }

    // host's master, added when it has none
    private PerMaster For(string host)
    {
        if (!_masters.TryGetValue(host, out var master)) _masters[host] = master = new PerMaster();
        return master;
    }

    private sealed record SelectionSince(string? PrimaryText, bool? PrimaryIsText);

    // the visit (the hash's Visit) whose hash emptied the clipboard for a push still to come
    private sealed record AwaitedPush(int? Visit);

    // where the change counter stood, null where the platform keeps none
    private sealed record ChangePoint(long? Count);

    // what the slave keeps per master
    private sealed class PerMaster
    {
        // the clock and the change counter when its visit began (none: one visit since the start)
        public long VisitFrom { get; set; } = -1;
        public long? VisitAt { get; set; }
        // the content hash last synced with it either way (answered to it, or applied from it), nothing new to it, and
        // the change counter then
        public ulong? SyncedWith { get; set; }
        public long? SyncedAt { get; set; }
        // the pull whose full answer last synced it, which a resend from it or an older pull answers again; null once a
        // push that wrote the clipboard synced it, or a pull matched a clipboard other than the synced one
        public int? SyncedBy { get; set; }
        // the pull whose full answer first carried that clipboard, which a resend of it names (Resends)
        public int? SyncedFirst { get; set; }
        // the clipboard its hash asked only the selection beside, so another master's push is never taken as that
        public ClipboardBaseline? SelectionPushFor { get; set; }
        // the selection taken while its stream was pending, which that stream's own must not overwrite
        public SelectionSince? SelectionSince { get; set; }
        // the hash of the concealed copy last applied from it, which its Cleared hash clears while still Hydra's write
        public ulong? ConcealedFrom { get; set; }
        // whether it was last answered with a concealed copy of this host's, told once that it was cleared here since
        public bool AnsweredConcealed { get; set; }
        // its hash emptied the clipboard here for a push still to come, and when
        public AwaitedPush? AwaitingPush { get; set; }
        public ChangePoint? EmptiedFor { get; set; }
    }
}
