using Cathedral.Extensions;
using Hydra.Relay;
using Microsoft.Extensions.Logging;

namespace Hydra.Screen;

// the entry hash a host waits for while the master's pulls to other hosts are outstanding, since their answers may
// change what the master offers: at most one host, the newest destination, waits on the pulls outstanding when it was
// entered, and whatever ends the wait returns that host once, so its hash goes exactly once. state under _lock
public sealed class DeferredClipboardOffer(TimeProvider time, ILogger log) : IDisposable
{
    // how long a pull's answer has to start before the waiting host stops waiting on it
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(1);

    private readonly Lock _lock = new();
    private Waiting? _waiting;
    // pulls whose wait ran out or whose answer was given up on, which no later entry waits for again
    private readonly HashSet<OutstandingPull> _settled = [];
    // former destinations told Pending, no longer waited for, still owed their hash once the streams they waited on end
    private readonly List<Owed> _owed = [];
    // hosts told Pending whose wait ended, to be sent their hash wherever the cursor is
    private readonly List<string> _due = [];
    // the newer pulls an answer is held for, each with its deadline: Wait after the answer was held, when it lands
    private readonly Dictionary<OutstandingPull, ITimer> _held = [];

    // the wait ran out with no answer begun: the waiting host
    public event Action<string>? TimedOut;

    // the pull an answer is held for was not answered within Wait of the hold: the held answer is to land
    public event Action<OutstandingPull>? HeldWaitedOut;

    // whether host's hash goes now, or waits on the outstanding pulls to other hosts, telling it a clipboard is pending;
    // owed: host is owed its hash (an owed read under way), which its wait then stays, sent wherever the cursor is;
    // streaming: whether a pull's answer streams in, which waits without a timer
    public DeferredOffer Defer(string host, IReadOnlyList<OutstandingPull> outstanding, Func<OutstandingPull, bool> streaming, bool owed = false)
    {
        bool pending;
        lock (_lock)
        {
            _settled.IntersectWith(outstanding);
            var waited = outstanding.Where(p => !p.Host.EqualsIgnoreCase(host) && !_settled.Contains(p)).ToList();
            if (waited.Count == 0) return DeferredOffer.SendNow;
            var waiting = new Waiting(host);
            owed |= OweFormer(host);
            foreach (var pull in waited)
            {
                // a pull the previous destination waited on keeps its timer
                var wait = _waiting?.Pulls.Find(w => w.Pull.Is(pull)) ?? new PullWait(pull);
                _waiting?.Pulls.Remove(wait);
                if (streaming(pull)) Stream(wait);
                else wait.Timer ??= time.CreateTimer(_ => Expire(wait), null, Wait, Timeout.InfiniteTimeSpan);
                waiting.Pulls.Add(wait);
            }
            Remove();
            _waiting = waiting;
            pending = waiting.Pulls.Any(w => w.Streaming);
            // a host already owed its hash stays owed it
            waiting.ToldPending = pending || owed;
        }
        if (log.IsEnabled(LogLevel.Debug))
            log.LogDebug("Clipboard hash to {Host} deferred until {Pulls} answered", host,
                string.Join(", ", outstanding.Where(p => !p.Host.EqualsIgnoreCase(host)).Select(p => $"{p.Host} ({p.Id})")));
        return pending ? DeferredOffer.WaitPending : DeferredOffer.Wait;
    }

    // nothing waits any more: the relay is gone, so nothing is owed either
    public void Clear()
    {
        lock (_lock)
        {
            Remove();
            _owed.Clear();
            _due.Clear();
            EndHeld(_ => true);
        }
    }

    // host's entry hash goes now: nothing waits on its behalf, while a former destination told Pending stays owed its hash
    // until the streams it waited on, of outstanding, end; whether host itself was owed its hash, which its entry's now is
    public bool SendingNow(string host, IReadOnlyList<OutstandingPull> outstanding)
    {
        lock (_lock)
        {
            var owed = OweFormer(host);
            Remove();
            End(w => !outstanding.Any(w.Pull.Is), settle: false);
            return owed;
        }
    }

    // the hosts told Pending whose wait has ended since, each once: owed their hash, wherever the cursor is
    public IReadOnlyList<string> TakeDue()
    {
        lock (_lock)
        {
            List<string> due = [.. _due];
            _due.Clear();
            return due;
        }
    }

    // under _lock: the waiting host, if told Pending and not host, stays owed its hash until the streams it waits on end;
    // host, entered now, is owed none apart from its entry's own: whether it was owed one (owed, due, or waiting told Pending)
    private bool OweFormer(string host)
    {
        var owed = _owed.RemoveAll(o => o.Host.EqualsIgnoreCase(host)) + _due.RemoveAll(h => h.EqualsIgnoreCase(host)) > 0;
        if (_waiting is not { ToldPending: true } former) return owed;
        if (former.Host.EqualsIgnoreCase(host)) return true;
        var streams = former.Pulls.FindAll(w => w.Streaming);
        if (streams.Count == 0) _due.Add(former.Host);
        else _owed.Add(new Owed(former.Host, streams));
        return owed;
    }

    // whether host's hash is waiting on a pull still outstanding
    public bool IsWaiting(string host)
    {
        lock (_lock)
            return _waiting != null && _waiting.Host.EqualsIgnoreCase(host);
    }

    // whether host is owed its hash here (waiting told Pending or owed, still owed once the streams it waited on end, or
    // due), which then goes once its wait ends
    public bool Owes(string host)
    {
        lock (_lock)
            return _waiting is { ToldPending: true } waiting && waiting.Host.EqualsIgnoreCase(host)
                || _owed.Exists(o => o.Host.EqualsIgnoreCase(host)) || _due.Contains(host, StringComparer.OrdinalIgnoreCase);
    }

    // host, waiting, is owed its hash (its push was aborted): sent wherever the cursor is once its wait ends
    public void Owe(string host)
    {
        lock (_lock)
            if (_waiting is { } waiting && waiting.Host.EqualsIgnoreCase(host)) waiting.ToldPending = true;
    }

    // the pull was answered, whole or by a completed stream, and the pulls its answer superseded will never be: the
    // waiting host, now offered the result, once it waits on nothing more
    public string? Answered(OutstandingPull answered, IReadOnlyList<OutstandingPull> superseded)
    {
        lock (_lock)
        {
            EndHeld(p => p.Is(answered) || superseded.Any(p.Is));
            return End(w => w.Pull.Is(answered) || superseded.Any(w.Pull.Is), settle: false);
        }
    }

    // of the pulls the waiting host waits on, those not in outstanding were answered meanwhile: the waiting host, once
    // it waits on nothing more
    public string? Resolve(IReadOnlyList<OutstandingPull> outstanding)
    {
        lock (_lock)
            return End(w => !outstanding.Any(w.Pull.Is), settle: false);
    }

    // the pull's answer began streaming: the waiting host, to be told a clipboard is pending, once; the wait on it now
    // lasts until the stream completes or is given up on
    public string? Streaming(string pullHost, int pullId)
    {
        var pull = new OutstandingPull(pullHost, pullId);
        lock (_lock)
        {
            if (_waiting is not { } waiting || waiting.Pulls.Find(w => w.Pull.Is(pull)) is not { } wait) return null;
            Stream(wait);
            if (waiting.ToldPending) return null;
            waiting.ToldPending = true;
            return waiting.Host;
        }
    }

    // under _lock: the wait on the pull now lasts until the stream completes or is given up on, so its timer goes
    private static void Stream(PullWait wait)
    {
        wait.Streaming = true;
        wait.Timer?.Dispose();
        wait.Timer = null;
    }

    // the pull host's answer will not come (its stream given up on, or it left; only streaming pulls with streamOnly, only
    // pullId's with pullId): the waiting host, once it waits on nothing more
    public string? Abandoned(string pullHost, bool streamOnly = false, int? pullId = null)
    {
        lock (_lock)
            return End(w => w.Pull.Host.EqualsIgnoreCase(pullHost) && (!streamOnly || w.Streaming) && (pullId == null || w.Pull.Id == pullId),
                settle: true);
    }

    // host, its owed read finding nothing to offer while the answers to streams still stream in, or answers are held for
    // held, is owed its hash again once they end, as a host told Pending is: a held one within its deadline. The held
    // ones waited out already, whose answers are to land now
    public IReadOnlyList<OutstandingPull> OweOn(string host, IReadOnlyList<OutstandingPull> streams, IReadOnlyList<OutstandingPull> held)
    {
        lock (_lock)
        {
            _owed.RemoveAll(o => o.Host.EqualsIgnoreCase(host));
            List<PullWait> waits = [.. streams.Select(p => new PullWait(p) { Streaming = true })];
            List<OutstandingPull> landNow = [];
            foreach (var pull in held)
            {
                if (_settled.Contains(pull)) landNow.Add(pull);
                else
                {
                    waits.Add(new PullWait(pull));
                    HoldUntil(pull);
                }
            }
            if (waits.Count > 0) _owed.Add(new Owed(host, waits));
            else _due.Add(host);
            return landNow;
        }
    }

    // the stream from newer's host completed and is held for newer, which is not streaming: what waited on the stream
    // waits on newer as on any pull, for Wait from now at most (HeldWaitedOut), unless newer was waited out already;
    // whether it was, when the held answer is to land at once
    public bool Held(OutstandingPull newer)
    {
        lock (_lock)
        {
            foreach (var owed in _owed) MoveTo(owed.Pulls, newer, streaming: false);
            if (_waiting is { } waiting) MoveTo(waiting.Pulls, newer, streaming: false);
            if (_settled.Contains(newer)) return true;
            HoldUntil(newer);
            return false;
        }
    }

    // under _lock: the answer held for newer lands Wait from now at most
    private void HoldUntil(OutstandingPull newer)
    {
        if (!_held.ContainsKey(newer)) _held[newer] = time.CreateTimer(_ => HeldExpired(newer), null, Wait, Timeout.InfiniteTimeSpan);
    }

    private void HeldExpired(OutstandingPull newer)
    {
        lock (_lock)
        {
            if (!_held.Remove(newer, out var timer)) return;
            timer.Dispose();
        }
        if (log.IsEnabled(LogLevel.Debug))
            log.LogDebug("Clipboard pull {PullId} to {PullHost} unanswered after {Wait}, the answer held for it lands", newer.Id, newer.Host, Wait);
        HeldWaitedOut?.Invoke(newer);
    }

    // under _lock: the held answer's deadline for pull goes, the pull resolved
    private void EndHeld(Predicate<OutstandingPull> ended)
    {
        foreach (var pull in _held.Keys.Where(p => ended(p)).ToList())
            if (_held.Remove(pull, out var timer)) timer.Dispose();
    }

    // pull's own answer began streaming, which replaced the answer held for it: that held answer's deadline goes, and the
    // waits on pull (the waiting host's, the owed hosts') wait as on a stream, ended with it however it ends
    public void HeldReplaced(OutstandingPull pull)
    {
        lock (_lock)
        {
            EndHeld(p => p.Is(pull));
            foreach (var wait in _owed.SelectMany(o => o.Pulls).Concat(_waiting?.Pulls ?? []).Where(w => w.Pull.Is(pull)))
                Stream(wait);
        }
    }

    // the stream from newer's host was aborted, superseded by that host's answer to newer, still on its way: what waited
    // on the stream waits on that answer instead
    public void Superseded(OutstandingPull newer)
    {
        lock (_lock)
        {
            foreach (var owed in _owed) MoveTo(owed.Pulls, newer, streaming: true);
            if (_waiting is { } waiting) MoveTo(waiting.Pulls, newer, streaming: true);
        }
    }

    // under _lock: the waits on newer's host's streams wait on newer instead, one already waiting on it as it was, else
    // until it is answered: a held one's within its deadline (Held), which lands it and so ends them
    private static void MoveTo(List<PullWait> pulls, OutstandingPull newer, bool streaming)
    {
        if (pulls.RemoveAll(w => w.Streaming && w.Pull.Host.EqualsIgnoreCase(newer.Host) && !w.Pull.Is(newer)) == 0) return;
        if (!pulls.Exists(w => w.Pull.Is(newer))) pulls.Add(new PullWait(newer) { Streaming = streaming });
    }

    // peers changed: abandons the wait on pulls whose hosts left, returning the waiting host once it waits on nothing more;
    // a host owed its hash that left is owed nothing
    public string? Retain(IReadOnlySet<string> present)
    {
        lock (_lock)
        {
            _owed.RemoveAll(o => !present.Contains(o.Host, StringComparer.OrdinalIgnoreCase));
            // a waiting host that left waits no more, and is owed nothing
            if (_waiting is { } waiting && !present.Contains(waiting.Host, StringComparer.OrdinalIgnoreCase)) Remove();
            var host = End(w => !present.Contains(w.Pull.Host, StringComparer.OrdinalIgnoreCase), settle: true);
            _due.RemoveAll(h => !present.Contains(h, StringComparer.OrdinalIgnoreCase));
            // a held answer lands as its host's pulls are forgotten
            EndHeld(p => !present.Contains(p.Host, StringComparer.OrdinalIgnoreCase));
            return host;
        }
    }

    public void Dispose() => Clear();

    private void Expire(PullWait expired)
    {
        string? host;
        bool heldFor;
        lock (_lock)
        {
            if (_waiting is not { } waiting || !waiting.Pulls.Contains(expired) || expired.Streaming) return;
            host = End(w => w == expired, settle: true);
            // an answer held for it lands now, not at its own deadline
            heldFor = _held.Remove(expired.Pull, out var deadline);
            deadline?.Dispose();
        }
        if (log.IsEnabled(LogLevel.Debug))
            log.LogDebug("Clipboard pull {PullId} to {PullHost} unanswered after {Wait}", expired.Pull.Id, expired.Pull.Host, Wait);
        if (heldFor) HeldWaitedOut?.Invoke(expired.Pull);
        if (host == null) return;
        if (log.IsEnabled(LogLevel.Debug))
            log.LogDebug("Offering {Host} the current clipboard hash", host);
        TimedOut?.Invoke(host);
    }

    // under _lock: stops waiting on the pulls that match, settling them if asked; the waiting host once it waits on
    // nothing more, and only if one of them ended its wait. A host told Pending whose wait ends this way is due its hash
    private string? End(Predicate<PullWait> ended, bool settle)
    {
        foreach (var owed in _owed.ToList())
        {
            if (owed.Pulls.RemoveAll(ended) == 0 || owed.Pulls.Count > 0) continue;
            _owed.Remove(owed);
            _due.Add(owed.Host);
        }
        if (_waiting is not { } waiting) return null;
        var gone = waiting.Pulls.FindAll(ended);
        if (gone.Count == 0) return null;
        foreach (var wait in gone)
        {
            wait.Timer?.Dispose();
            waiting.Pulls.Remove(wait);
            if (settle) _settled.Add(wait.Pull);
        }
        if (waiting.Pulls.Count > 0) return null;
        _waiting = null;
        if (waiting.ToldPending) _due.Add(waiting.Host);
        return waiting.Host;
    }

    // under _lock; a timer callback already in flight finds its wait gone and does nothing
    private void Remove()
    {
        foreach (var wait in _waiting?.Pulls ?? [])
            wait.Timer?.Dispose();
        _waiting = null;
    }

    private sealed class Waiting(string host)
    {
        public string Host { get; } = host;
        public List<PullWait> Pulls { get; } = [];
        // whether the host has been told a clipboard is pending
        public bool ToldPending { get; set; }
    }

    private sealed class PullWait(OutstandingPull pull)
    {
        public OutstandingPull Pull { get; } = pull;
        public bool Streaming { get; set; }
        public ITimer? Timer { get; set; }
    }

    // a host told Pending, owed its hash once the streams it waited on (no timers) end
    private sealed record Owed(string Host, List<PullWait> Pulls);
}

// what an entry's clipboard hash does while a pull is outstanding: goes now, waits, or waits with the host told a
// clipboard is pending
public enum DeferredOffer { SendNow, Wait, WaitPending }
