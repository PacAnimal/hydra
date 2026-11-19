using System.Text;
using Hydra.Relay;
using Microsoft.Extensions.Logging;
using Tmds.DBus.Protocol;

namespace Hydra.Platform.Linux.Wayland;

// The Wayland clipboard through the portal's Clipboard interface on the run's portal session. A write offers every
// format at once (SetSelection) and answers each SelectionTransfer from the bytes it keeps; a read takes the MIME types
// the last SelectionOwnerChanged listed, each through SelectionRead. The portal has no primary selection, so this keeps
// a CopiedPrimary as macOS and Windows do. Without a portal clipboard every call goes to wl-clipboard
// (WaylandClipboardSync), or to nothing (NullClipboardSync) when that is missing too.
//
// The change counter moves on Hydra's own writes and on each owner change to another client, an owner gone (no types)
// excepted. Notices are delivered in order, so one arriving while a write is under way, or after it until the portal
// says the session owns the selection (OwnNoticeWait at most), is older than the write and is passed over; a write that
// failed or was never confirmed takes them after all. Until the first owner change since the session started, the
// types are unknown (the portal never announces the selection already there), so a read asks for each as it stands.
//
// On an InputCapture session the portal sets the selection only while a capture is on, so each visit claims it while
// it may (CaptureActive): unless the session owns it already, the master's clipboard is read whole (its own copy) and
// the selection set to the types that copy can be served as (its text as every text type), never one it lacks; while
// the counter stays where the last claim left it (no copy, no write), that claim's copy is set again unread. A concealed
// copy is left with its owner. A write while the session holds the selection only replaces what is served, fitted to
// the offer (extra types dropped, html and rtf made from its text, an owner's type it lacks refused), so a return's
// pull answer landing after the release is still pasted; one of another kind (text for an offered image, or the
// reverse) cannot be served and fails. A transfer waits a little for the latest pull to settle (TransferWait, once per pull),
// then serves the newest: that answer, else the own copy. The own copy is the user's, not Hydra's write: it reads back
// as itself, moves no counter, and allows no echo fallback.
internal sealed class PortalClipboardSync : IClipboardSync, IPullObserver, ICaptureClipboard, IAsyncDisposable
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);
    // how long a call waits for the portal session before taking the clipboard as unknown
    private static readonly TimeSpan ConnectWait = TimeSpan.FromSeconds(2);
    // how long after a write the owner changes not yet confirming it are taken as older than it
    private static readonly TimeSpan OwnNoticeWait = TimeSpan.FromSeconds(1);
    // how long a transfer waits for the latest pull to settle before serving what it has
    internal static readonly TimeSpan TransferWait = TimeSpan.FromSeconds(3);

    private readonly Func<CancellationToken, Task<IPortalClipboard?>> _clipboard;
    private readonly Func<IClipboardSync> _fallbackFactory;
    private readonly TimeProvider _time;
    // set once, and a logger is safe on any thread: read without either lock
    private ILogger Log { get; }
    private readonly IWaylandSession? _session;
    private readonly CopiedPrimary _primary;
    private readonly CancellationTokenSource _stop = new();
    // _stop's token, kept so the reads and the connect can take it without touching _stop, which DisposeAsync disposes
    private readonly CancellationToken _stopping;

    // reads take turns, so one sees one owner's formats
    private readonly Lock _readGate = new();
    // one setting of the selection at a time, never held across a read; taken before _readGate and _lock
    private readonly Lock _selectGate = new();
    // everything below
    private readonly Lock _lock = new();
    private IPortalClipboard? _portal;
    private IClipboardSync? _fallback;
    private Task? _connecting;
    private int _epoch;
    private bool _everConnected;
    private bool _disposed;

    // the selection as the last owner change left it: whether the session owns it, and the types another client offers
    // (null while unknown)
    private bool _owned;
    private IReadOnlyList<string>? _types;
    // moved by every owner change taken and every write, so a read straddling one is read again
    private long _generation;
    // the last whole read and the generation it was read at: the selection's read of the same copy takes it, so one
    // ReadAll and its GetPrimaryText never ask the owner twice
    private GenerationRead? _lastRead;
    private long _count;
    // owner changes until then are older than Hydra's write; those passed over, kept for a write that fails
    private DateTimeOffset _ownNoticeUntil = DateTimeOffset.MinValue;
    private bool _confirmPending;
    // the count a write the portal confirmed mid-call left
    private long? _confirmedAt;
    // whether the selection under way is a write, which counts when the portal confirms it mid-call, or a claim, which
    // never counts
    private bool _confirmCounts;
    // whether the portal confirmed the selection under way during its call
    private bool _confirmedDuring;
    private int _heldChanges;
    private IReadOnlyList<string>? _heldTypes;

    // what the session's selection serves, by MIME type: Hydra's write, or the own copy a claim read
    private Dictionary<string, byte[]> _serving = new(StringComparer.Ordinal);
    // the master's own clipboard as the claim read it, while the session serves it and no write replaced it
    private ClipboardRead? _ownCopy;
    // the types the selection was last set with, which are those it serves
    private HashSet<string> _claimed = new(StringComparer.Ordinal);
    // the last claim's read, taken again while the counter stays where it was then (no copy and no write since)
    private ClaimedRead? _lastClaim;
    // an input capture lends this clipboard its visits (CaptureActive): a write the portal refuses only replaces what
    // is served, fitted to the session's offer
    private bool _claims;
    // the settling of the latest pull the master sent, which a transfer waits for once
    private Task _latestPull = Task.CompletedTask;
    // whether a transfer already waited the latest pull out: the ones after it serve at once
    private bool _pullWaitedOut;

    public PortalClipboardSync(IWaylandSession session, ILogger<PortalClipboardSync> log)
        : this(async cancel => await session.Clipboard(cancel), () => CreateFallback(log), TimeProvider.System, log)
    {
        _session = session;
        session.SessionLost += OnSessionLost;
    }

    // tests: the portal's clipboard and the fallback from elsewhere
    internal PortalClipboardSync(Func<CancellationToken, Task<IPortalClipboard?>> clipboard, Func<IClipboardSync> fallback, TimeProvider time,
        ILogger log)
    {
        _clipboard = clipboard;
        _fallbackFactory = fallback;
        _time = time;
        Log = log;
        _stopping = _stop.Token;
        _primary = new CopiedPrimary(Count, ReadTextCopy, log);
    }

    private static IClipboardSync CreateFallback(ILogger log)
    {
        if (WaylandClipboardSync.Available) return new WaylandClipboardSync(log);
        if (log.IsEnabled(LogLevel.Warning)) log.LogWarning("Neither the portal nor wl-clipboard offers the clipboard; it is not synced");
        return new NullClipboardSync();
    }

    // whether the portal's clipboard, or the sync in its place, is there, waiting a little for it
    internal bool Connected() => Resolve(wait: true) != null;

    public ClipboardRead ReadAll() => Resolve(wait: true) switch
    {
        { Fallback: { } fallback } => fallback.ReadAll(),
        { Portal: { } portal } => ReadPortal(portal),
        _ => throw new ClipboardUnreadableException("the portal's clipboard is not connected"),
    };

    public string? GetPrimaryText() => Resolve(wait: true) switch
    {
        { Fallback: { } fallback } => fallback.GetPrimaryText(),
        { Portal: not null } => _primary.Read(),
        _ => null,
    };

    public bool SetPrimaryText(string text) => Resolve(wait: true) switch
    {
        { Fallback: { } fallback } => fallback.SetPrimaryText(text),
        { Portal: not null } => _primary.Relay(text),
        _ => false,
    };

    public bool HasFileClipboard() => Resolve(wait: false) is { Fallback: { } fallback } && fallback.HasFileClipboard();

    public bool CanUseEchoFallback()
    {
        if (Resolve(wait: false) is { Fallback: { } fallback }) return fallback.CanUseEchoFallback();
        lock (_lock) return OwnsWrite();
    }

    public long? ChangeCount()
    {
        if (Resolve(wait: false) is { Fallback: { } fallback }) return fallback.ChangeCount();
        lock (_lock)
        {
            if (!_everConnected) return null;
            Settle();
            return _count;
        }
    }

    public long? WroteAt() => Resolve(wait: false) is { Fallback: { } fallback } ? fallback.WroteAt() : _primary.WrittenAt();

    public ClipboardSnapshot? SetClipboard(ClipboardSnapshot contents)
    {
        switch (Resolve(wait: true))
        {
            case { Fallback: { } fallback }:
                return fallback.SetClipboard(contents);
            case { Portal: { } portal }:
                ClipboardSnapshot? written = null;
                var replaced = _primary.Write(contents.PrimaryText, leftAt =>
                {
                    written = Offer(portal, contents, leftAt);
                    return written != null;
                });
                return replaced ? written : null;
            default:
                return null;
        }
    }

    public bool ClearClipboard() =>
        Resolve(wait: false) is { Fallback: { } fallback } ? fallback.ClearClipboard() : SetClipboard(ClipboardSnapshot.Placeholder) != null;

    // the latest pull the master sent, which a transfer waits for before serving
    public void PullSent(Task settled)
    {
        lock (_lock)
        {
            _latestPull = settled;
            _pullWaitedOut = false;
        }
    }

    // A capture is on: unless the session holds the selection already, the master's own clipboard is read whole and the
    // selection claimed for the types it can be served as, served from that copy until a write replaces it or another
    // client takes the selection. A clipboard that cannot be read, holds nothing, or holds a concealed copy is not
    // claimed. Never throws
    public void Claim()
    {
        try
        {
            if (Resolve(wait: true) is not { Portal: { } portal }) return;
            ClaimFor(portal);
        }
        catch (Exception e)
        {
            if (Log.IsEnabled(LogLevel.Warning)) Log.LogWarning(e, "Claiming the clipboard for the visit failed");
        }
    }

    // The reads run outside _selectGate, so a write waiting on it (a pull answer, on the relay's receive path) waits for
    // the setting alone; the setting is skipped when an owner change, a write or a new session landed since the read.
    // Tried again once when one did
    private void ClaimFor(IPortalClipboard portal)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            ClaimedRead? claim;
            long expected;
            lock (_lock)
            {
                _claims = true;
                // the session the claim began on is gone
                if (!ReferenceEquals(_portal, portal)) return;
                // Hydra's write or an earlier claim still stands, offering what it serves
                if (Owns()) return;
                // no copy and no write since the last claim: its read is still the master's clipboard
                claim = attempt == 0 && _lastClaim is { } last && last.Count == _count ? last : null;
                expected = _generation;
            }

            if (claim == null)
            {
                ClipboardRead own;
                try
                {
                    own = ReadPortal(portal);
                }
                catch (ClipboardUnreadableException e)
                {
                    if (Log.IsEnabled(LogLevel.Debug)) Log.LogDebug(e, "The clipboard could not be read for the visit; not claiming it");
                    return;
                }
                if (own.Concealed)
                {
                    if (Log.IsEnabled(LogLevel.Debug)) Log.LogDebug("The clipboard holds a concealed copy; leaving it with its owner for the visit");
                    return;
                }
                // the generation the read saw: the one it was kept at, else (nothing there to read) the one before it
                lock (_lock) expected = _lastRead is { } read && ReferenceEquals(read.Read, own) && read.Generation >= expected ? read.Generation : expected;
                var serving = ClaimServing(portal, own);
                if (serving.Count == 0)
                {
                    if (Log.IsEnabled(LogLevel.Debug)) Log.LogDebug("The clipboard holds nothing to serve; not claiming it for the visit");
                    return;
                }
                claim = new ClaimedRead(0, own, serving);
            }

            bool selected;
            Exception? failed;
            lock (_selectGate) selected = Select(portal, claim.Served, claim, expected, out _, out failed);
            if (selected)
            {
                if (Log.IsEnabled(LogLevel.Debug)) Log.LogDebug("Claimed the clipboard for the visit, serving the master's own copy");
                return;
            }
            if (failed != null)
            {
                if (Log.IsEnabled(LogLevel.Debug)) Log.LogDebug(failed, "The portal refused the clipboard claim; the visit keeps the clipboard as it is");
                return;
            }
        }
        if (Log.IsEnabled(LogLevel.Debug)) Log.LogDebug("The clipboard kept changing while read for the visit; not claiming it");
    }

    // what a claim serves of the master's own copy: each format it holds, its text as every text type, and the rest of
    // the owner's offer
    private Dictionary<string, byte[]> ClaimServing(IPortalClipboard portal, ClipboardRead own)
    {
        var serving = Serving(Snapshot(own));
        if (serving.GetValueOrDefault(WaylandMime.OfferedText[0]) is { } text)
            foreach (var type in WaylandMime.Text)
                serving[type] = text;
        KeepOthers(portal, serving, own);
        return serving;
    }

    // a read of the clipboard as the snapshot a write of it serves
    private static ClipboardSnapshot Snapshot(ClipboardRead read) => new(read.Text, null, read.ImagePng, read.Html, read.Rtf, read.Concealed);

    // The rest of the owner's offer, so a claim loses none of the master's own formats: the X11 text atoms served as its
    // text, and every other type (a file copy's URI list, another image type) read as bytes and served as read, within
    // the clipboard's cap. One that cannot be read is left out
    private void KeepOthers(IPortalClipboard portal, Dictionary<string, byte[]> serving, ClipboardRead own)
    {
        IReadOnlyList<string>? offered;
        lock (_lock) offered = _types;
        if (offered == null) return;
        var text = serving.GetValueOrDefault(WaylandMime.OfferedText[0]);
        long left = ClipboardUtils.MaxClipboardBytes - serving.Values.Distinct().Sum(v => (long)v.Length);
        foreach (var type in offered.Distinct(StringComparer.Ordinal))
        {
            if (serving.ContainsKey(type)) continue;
            if (WaylandMime.Text.Contains(type))
            {
                if (text != null) serving[type] = text;
                continue;
            }
            if (Probed.Contains(type)) continue;
            // no text served as anything but text, and nothing of a concealed copy beyond what Hydra carries
            if (own.Concealed || left <= 0) continue;
            try
            {
                if (Read(portal, type, (int)Math.Min(left, int.MaxValue)) is { Length: > 0 } data)
                {
                    serving[type] = data;
                    left -= data.Length;
                }
            }
            catch (ClipboardUnreadableException e)
            {
                if (Log.IsEnabled(LogLevel.Debug)) Log.LogDebug(e, "The clipboard's {MimeType} could not be read for the visit; it is left out", type);
            }
        }
    }

    // -- the portal's side --

    // under _lock: the session holds the selection, as Hydra's write or a claim's own copy
    private bool Owns()
    {
        Settle();
        return _owned && _portal != null;
    }

    // under _lock: the clipboard is still Hydra's write, not the master's own copy a claim serves
    private bool OwnsWrite() => Owns() && _ownCopy == null;

    private long Count()
    {
        lock (_lock)
        {
            Settle();
            return _count;
        }
    }

    // under _lock: owner changes held past a write the portal never confirmed are copies after all
    private void Settle()
    {
        if (!_confirmPending || _time.GetUtcNow() < _ownNoticeUntil) return;
        _confirmPending = false;
        if (_heldChanges > 0) Take(_heldTypes ?? [], _heldChanges);
        _heldChanges = 0;
        _heldTypes = null;
    }

    // what the portal holds as another client offers it, against one owner: read again once when it changed meanwhile
    private ClipboardRead ReadPortal(IPortalClipboard portal)
    {
        lock (_readGate)
            return ReadOnce(portal) ?? ReadOnce(portal) ?? throw new ClipboardUnreadableException("the clipboard changed while it was read, twice");
    }

    // null when the owner changed during the read
    private ClipboardRead? ReadOnce(IPortalClipboard portal)
    {
        HashSet<string>? offered;
        long generation;
        lock (_lock)
        {
            // the master's own copy a claim serves reads as itself; Hydra's write reads as nothing
            if (Owns()) return _ownCopy ?? ClipboardRead.Empty;
            offered = _types?.ToHashSet(StringComparer.Ordinal);
            generation = _generation;
        }
        if (offered == null) return Probe(portal, generation);
        if (offered.Count == 0) return ClipboardRead.Empty;

        return ReadKept(() => WaylandMime.ReadOffered(offered, (type, max) => Read(portal, type, max), Log), generation);
    }

    // The read, kept as Kept keeps it; null too when it could not be read because an owner change, a claim or a write
    // landed during it (mutter refuses to read the session's own selection once a claim's lands), so it is read again
    private ClipboardRead? ReadKept(Func<ClipboardRead> read, long generation)
    {
        ClipboardRead result;
        try
        {
            result = read();
        }
        catch (ClipboardUnreadableException e) when (Changed(generation))
        {
            if (Log.IsEnabled(LogLevel.Debug)) Log.LogDebug(e, "The clipboard changed while it was read; reading it again");
            return null;
        }
        return Kept(result, generation);
    }

    // a selection being set counts too: mutter can refuse the read before the set returns or is confirmed
    private bool Changed(long generation)
    {
        lock (_lock) return _generation != generation || _confirmPending;
    }

    // the read, when no owner change or write landed during it, kept for the selection's read of the same copy; null else
    private ClipboardRead? Kept(ClipboardRead read, long generation)
    {
        lock (_lock)
        {
            if (_generation != generation) return null;
            _lastRead = new GenerationRead(generation, read);
            return read;
        }
    }

    // every type a probe asks for
    private static HashSet<string> Probed { get; } = new([.. WaylandMime.Text, WaylandMime.Png, WaylandMime.Html, WaylandMime.Rtf, WaylandMime.PasswordHint], StringComparer.Ordinal);

    // No owner change seen since the session started (the portal never announces the selection already there), so its
    // types are unknown: each is asked for as it stands, one that is not there read as absent (Absent); none there is
    // an empty clipboard. Null when the owner changed during the probe
    private ClipboardRead? Probe(IPortalClipboard portal, long generation) =>
        ReadKept(() => WaylandMime.ReadOffered(Probed, (type, max) => ReadIfThere(portal, type, max), Log), generation);

    // one MIME type of the selection, null when it is not there (or runs past the cap); throws when it cannot be read
    private byte[]? ReadIfThere(IPortalClipboard portal, string mimeType, int maxBytes)
    {
        try
        {
            return Read(portal, mimeType, maxBytes) is { Length: > 0 } data ? data : null;
        }
        catch (ClipboardUnreadableException e) when (Absent(e.InnerException))
        {
            return null;
        }
    }

    // What the portal answers for a selection that is not there: mutter's "No selection owner available" reaches us as
    // org.freedesktop.portal.Error.Failed "Internal error" (xdg-desktop-portal-gnome rewrites it "Failed to selection
    // read", the frontend again "Internal error"), and an owner without the type gives a pipe that closes empty, read as
    // no bytes. Anything else (access denied, a lost bus, a timeout, a broken pipe) is a clipboard that could not be read.
    // Known blur: the rewritten message is the same for every backend failure, so a real one before the first owner
    // change reads as empty, not unreadable
    private static bool Absent(Exception? e) => e is DBusErrorReplyException { ErrorName: "org.freedesktop.portal.Error.Failed" };

    // CopiedPrimary's readTextCopy: the text of another client's copy, null for an image or a concealed copy
    private string? ReadTextCopy()
    {
        IPortalClipboard? portal;
        lock (_lock) portal = _portal;
        if (portal == null) throw new ClipboardUnreadableException("the portal's clipboard is not connected");

        lock (_readGate)
        {
            // the copy ReadAll just read, when nothing has changed since; else read as it stands (ReadOnce answers nothing
            // for Hydra's own write), a copy changing under the read tried again on the next one
            ClipboardRead? read;
            lock (_lock)
            {
                if (Owns()) return _ownCopy is { } own ? TextCopy(own) : null;
                read = _lastRead is { } last && last.Generation == _generation ? last.Read : null;
            }
            read ??= ReadOnce(portal) ?? throw new ClipboardUnreadableException("the clipboard changed while it was read");
            return TextCopy(read);
        }
    }

    private static string? TextCopy(ClipboardRead read) => read.ImagePng != null || read.Concealed ? null : read.Text;

    // one MIME type of the selection, at most maxBytes (int.MaxValue: the clipboard's cap); null when it runs past that
    private byte[]? Read(IPortalClipboard portal, string mimeType, int maxBytes)
    {
        var max = (int)Math.Min(maxBytes, ClipboardUtils.MaxClipboardBytes);
        byte[]? data;
        try
        {
            data = portal.Read(mimeType, max, ReadTimeout, _stopping).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            throw new ClipboardUnreadableException($"the portal's {mimeType} could not be read", e);
        }
        if (data == null && Log.IsEnabled(LogLevel.Warning))
            Log.LogWarning("The clipboard's {MimeType} is over {Max} bytes; it is left out", mimeType, max);
        return data;
    }

    // Hydra's write: every format at once, the bytes kept to answer each transfer; null when the portal refused it. One
    // the portal refuses (the capture released) still lands, fitted to it, while a claim's selection stands: it
    // replaces what is served
    private ClipboardSnapshot? Offer(IPortalClipboard portal, ClipboardSnapshot contents, Action<long> leftAt)
    {
        var serving = Serving(contents);
        // nothing for the clipboard: left as it is, never cleared through an empty offer; CopiedPrimary keeps the selection
        if (serving.Count == 0) return contents;
        lock (_selectGate)
        {
            bool claims;
            lock (_lock) claims = _claims;
            if (Select(portal, serving, claim: null, expected: null, out var at, out var failed))
            {
                leftAt(at);
                return contents;
            }
            if (claims && Replace(serving, contents.IsClear, out at))
            {
                if (Log.IsEnabled(LogLevel.Debug))
                    Log.LogDebug("The portal sets the clipboard only during a capture; the session's selection serves the write");
                leftAt(at);
                return contents;
            }
            if (Log.IsEnabled(LogLevel.Warning)) Log.LogWarning(failed, "The portal refused the clipboard");
            return null;
        }
    }

    // Under _selectGate: the write served from the selection the session holds, fitted to what it offers, so nothing
    // offered is served from an older copy: its text as every text type offered, html and rtf made from that text when
    // offered and missing, extra types dropped, an offered type it cannot fill (an owner's URI list) refused at its
    // transfer. Not served (false) when it fills none of an offered kind: no text for offered text, no image for an
    // offered image. A clear's no-history hint goes with it only where offered: without it the placeholder text alone
    // is served, still Hydra's write and read back as no copy. Hydra's write as any other, counted
    private bool Replace(Dictionary<string, byte[]> serving, bool clear, out long at)
    {
        lock (_lock)
        {
            at = 0;
            if (!Owns()) return false;
            if (clear && !_claimed.Contains(WaylandMime.PasswordHint))
            {
                serving = new Dictionary<string, byte[]>(serving, StringComparer.Ordinal);
                serving.Remove(WaylandMime.PasswordHint);
            }
            if (Fit(serving, _claimed) is not { } replaced) return false;
            _serving = replaced;
            _ownCopy = null;
            _generation++;
            at = ++_count;
            return true;
        }
    }

    // what of the write the offer can serve, by offered type; null when it leaves an offered kind empty
    private Dictionary<string, byte[]>? Fit(Dictionary<string, byte[]> serving, HashSet<string> offered)
    {
        var text = serving.GetValueOrDefault(WaylandMime.OfferedText[0]);
        var fitted = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var type in offered)
        {
            if (serving.TryGetValue(type, out var data)) fitted[type] = data;
            else if (text != null && WaylandMime.Text.Contains(type)) fitted[type] = text;
            else if (text != null && type == WaylandMime.Html) fitted[type] = SynthesisedHtml(Encoding.UTF8.GetString(text));
            else if (text != null && type == WaylandMime.Rtf) fitted[type] = SynthesisedRtf(Encoding.UTF8.GetString(text));
        }

        var textOffered = offered.Any(WaylandMime.Text.Contains);
        var imageOffered = offered.Any(IsImage);
        if ((textOffered && !fitted.Keys.Any(WaylandMime.Text.Contains)) || (imageOffered && !fitted.Keys.Any(IsImage))
            || (serving.ContainsKey(WaylandMime.PasswordHint) && !offered.Contains(WaylandMime.PasswordHint)))
        {
            if (Log.IsEnabled(LogLevel.Debug))
                Log.LogDebug("The write ({Formats}) is not of the kind the session's selection offers ({Offered}); it cannot be served",
                    string.Join(", ", serving.Keys), string.Join(", ", offered));
            return null;
        }

        if (Log.IsEnabled(LogLevel.Debug))
        {
            var dropped = serving.Keys.Where(t => !offered.Contains(t)).ToList();
            if (dropped.Count > 0)
                Log.LogDebug("The session's selection does not offer {Dropped}; the write is served without them", string.Join(", ", dropped));
            var lacking = offered.Where(t => !fitted.ContainsKey(t)).ToList();
            if (lacking.Count > 0)
                Log.LogDebug("The write has no {Lacking}; a paste of those is refused", string.Join(", ", lacking));
        }
        return fitted;
    }

    private static bool IsImage(string type) => type.StartsWith("image/", StringComparison.Ordinal);

    // text as html: escaped inside a minimal fragment, its line breaks kept
    internal static byte[] SynthesisedHtml(string text)
    {
        var html = new StringBuilder("<meta charset=\"utf-8\"><div>");
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '&': html.Append("&amp;"); break;
                case '<': html.Append("&lt;"); break;
                case '>': html.Append("&gt;"); break;
                case '"': html.Append("&quot;"); break;
                case '\'': html.Append("&#39;"); break;
                case '\r' when i + 1 < text.Length && text[i + 1] == '\n': break;
                case '\r' or '\n': html.Append("<br>"); break;
                default: html.Append(c); break;
            }
        }
        return Encoding.UTF8.GetBytes(html.Append("</div>").ToString());
    }

    // text as minimal rtf: braces and backslashes escaped, line breaks as paragraphs, every non-ASCII UTF-16 unit as
    // \uN? (N signed, as RTF has it)
    internal static byte[] SynthesisedRtf(string text)
    {
        var rtf = new StringBuilder(@"{\rtf1\ansi\uc1 ");
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '\\' or '{' or '}': rtf.Append('\\').Append(c); break;
                case '\r' when i + 1 < text.Length && text[i + 1] == '\n': break;
                case '\r' or '\n': rtf.Append(@"\par "); break;
                case '\t': rtf.Append(@"\tab "); break;
                case < ' ': break;
                case > '\x7f': rtf.Append(@"\u").Append(((short)c).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('?'); break;
                default: rtf.Append(c); break;
            }
        }
        return Encoding.ASCII.GetBytes(rtf.Append('}').ToString());
    }

    // Under _selectGate: sets the selection to the types serving holds, served from it. A write (no claim) is Hydra's
    // and moves the counter, at the count it left; a claim moves none, serves its own copy, and is not set when an owner
    // change or write landed since the read at expected. Nothing is kept of a selection set on a session lost meanwhile.
    // False, with the failure when there was one, when the selection was not set
    private bool Select(IPortalClipboard portal, Dictionary<string, byte[]> serving, ClaimedRead? claim, long? expected, out long at,
        out Exception? failed)
    {
        at = 0;
        failed = null;
        var counts = claim == null;
        Dictionary<string, byte[]> previous;
        int epoch;
        lock (_lock)
        {
            if (!ReferenceEquals(_portal, portal)) return false;
            if (expected is { } generation && generation != _generation) return false;
            epoch = _epoch;
            previous = _serving;
            _serving = serving;
            _confirmPending = true;
            _confirmCounts = counts;
            _confirmedDuring = false;
            _confirmedAt = null;
            _ownNoticeUntil = DateTimeOffset.MaxValue;
            _heldChanges = 0;
            _heldTypes = null;
        }

        try
        {
            portal.SetSelection([.. serving.Keys]).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            failed = e;
        }

        lock (_lock)
        {
            // the session the selection was set on is gone, and with it the selection: the next one starts afresh
            if (epoch != _epoch) return false;
            if (failed != null)
            {
                // the old selection still stands, and the owner changes passed over are copies after all
                _serving = previous;
                if (_confirmPending && _heldChanges > 0) Take(_heldTypes ?? [], _heldChanges);
                _confirmPending = false;
                _ownNoticeUntil = DateTimeOffset.MinValue;
                _heldChanges = 0;
                _heldTypes = null;
            }
            else
            {
                if (_confirmedDuring)
                {
                    // the portal confirmed it during the call, and counted a write then
                    if (_confirmedAt is { } confirmed) at = confirmed;
                }
                else
                {
                    // owner changes during the call are older than the selection; later ones may be too, until the
                    // portal confirms it or a little while passes
                    _owned = true;
                    _generation++;
                    _heldChanges = 0;
                    _heldTypes = null;
                    _ownNoticeUntil = _time.GetUtcNow() + OwnNoticeWait;
                    if (counts) at = ++_count;
                }
                _claimed = new HashSet<string>(serving.Keys, StringComparer.Ordinal);
                _ownCopy = claim?.Read;
                if (claim != null) _lastClaim = claim with { Count = _count };
            }
            _confirmedAt = null;
            _confirmedDuring = false;
        }
        return failed == null;
    }

    // every MIME type the snapshot can be served as; the concealed copy and the placeholder carry KDE's hint, so
    // clipboard managers keep neither
    private static Dictionary<string, byte[]> Serving(ClipboardSnapshot contents)
    {
        var serving = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(contents.Text))
        {
            var text = Encoding.UTF8.GetBytes(contents.Text);
            foreach (var type in WaylandMime.OfferedText) serving[type] = text;
        }
        if (contents.Html != null) serving[WaylandMime.Html] = Encoding.UTF8.GetBytes(contents.Html);
        if (contents.Rtf != null) serving[WaylandMime.Rtf] = contents.Rtf;
        if (contents.ImagePng != null) serving[WaylandMime.Png] = contents.ImagePng;
        if (contents.Concealed || contents.IsClear) serving[WaylandMime.PasswordHint] = WaylandMime.Secret;
        return serving;
    }

    private async Task OnTransfer(string mimeType, uint serial)
    {
        IPortalClipboard? portal;
        Task pull;
        lock (_lock)
        {
            portal = _portal;
            pull = _latestPull;
            if (_pullWaitedOut) pull = Task.CompletedTask;
        }
        if (portal == null) return;
        // a return's pull answer may still be on its way, and is newer than what is served now; one never settling is
        // waited out once, not once per transfer
        if (!pull.IsCompleted)
        {
            try
            {
                await pull.WaitAsync(TransferWait, _time, _stopping);
            }
            catch (TimeoutException)
            {
                lock (_lock)
                    if (ReferenceEquals(pull, _latestPull))
                        _pullWaitedOut = true;
                if (Log.IsEnabled(LogLevel.Debug)) Log.LogDebug("The clipboard's pull did not settle in time; serving {MimeType} as it stands", mimeType);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                return;
            }
        }
        byte[]? data;
        lock (_lock)
        {
            // another client took the selection meanwhile: what the session served is no longer the clipboard
            data = ReferenceEquals(portal, _portal) && _owned ? _serving.GetValueOrDefault(mimeType) : null;
        }
        if (data != null) await portal.Write(serial, data);
        else await portal.SelectionWriteDone(serial, false);
    }

    private void OnOwnerChanged(IReadOnlyList<string> types, bool sessionIsOwner)
    {
        if (Log.IsEnabled(LogLevel.Debug))
            Log.LogDebug("Clipboard owner changed: {Types}, this session: {Owner}", types.Count == 0 ? "none" : string.Join(", ", types), sessionIsOwner);
        lock (_lock)
        {
            if (sessionIsOwner)
            {
                // Hydra's selection, confirmed: what came before it is older still. Mid-call, a write is counted now, so
                // a copy confirmed after it moves past it; a claim moves no counter
                if (_confirmPending && _ownNoticeUntil == DateTimeOffset.MaxValue)
                {
                    _confirmedDuring = true;
                    if (_confirmCounts) _confirmedAt = ++_count;
                }
                _owned = true;
                _generation++;
                _confirmPending = false;
                _ownNoticeUntil = DateTimeOffset.MinValue;
                _heldChanges = 0;
                _heldTypes = null;
                return;
            }
            if (_time.GetUtcNow() < _ownNoticeUntil)
            {
                _heldChanges++;
                _heldTypes = types;
                return;
            }
            Settle();
            Take(types, 1);
        }
    }

    // under _lock: another client's taking, counted unless it left no types (an owner gone is no copy)
    private void Take(IReadOnlyList<string> types, int changes)
    {
        _owned = false;
        _ownCopy = null;
        _types = types;
        _generation++;
        if (types.Count > 0) _count += changes;
    }

    // -- connecting --

    // the portal's clipboard once connected, else the sync standing in for it; neither while still connecting
    private Backend? Resolve(bool wait)
    {
        Task connecting;
        lock (_lock)
        {
            if (_disposed || Current() != null) return Current();
            if (_connecting == null)
            {
                var epoch = _epoch;
                _connecting = Task.Run(() => Connect(epoch), CancellationToken.None);
            }
            connecting = _connecting;
        }
        if (!wait || !connecting.Wait(ConnectWait)) return null;   // Connect never throws
        lock (_lock) return Current();
    }

    // under _lock
    private Backend? Current() =>
        _fallback != null ? new Backend(null, _fallback) : _portal != null ? new Backend(_portal, null) : null;

    private async Task Connect(int epoch)
    {
        IPortalClipboard? portal;
        try
        {
            portal = await _clipboard(_stopping);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e)
        {
            // the session could not be had this time: unknown until a later call tries again
            if (Log.IsEnabled(LogLevel.Warning)) Log.LogWarning(e, "The portal's clipboard could not be had; trying again on the next use");
            lock (_lock)
                if (epoch == _epoch)
                    _connecting = null;
            return;
        }

        IClipboardSync fallback;
        lock (_lock)
        {
            if (_disposed || epoch != _epoch) return;
            if (portal != null)
            {
                _portal = portal;
                _everConnected = true;
                portal.SelectionOwnerChanged += OnOwnerChanged;
                portal.SelectionTransfer += OnTransfer;
                return;
            }
            fallback = _fallback = _fallbackFactory();
        }
        if (Log.IsEnabled(LogLevel.Information))
            Log.LogInformation("The portal offers no clipboard; using {Fallback}", fallback.GetType().Name);
    }

    // the session is gone, and with it the selection Hydra offered (an owner gone, no copy): unknown until the next
    // session reports an owner
    internal void OnSessionLost()
    {
        lock (_lock)
        {
            Detach();
            _epoch++;
            _connecting = null;
            _owned = false;
            _ownCopy = null;
            _lastClaim = null;
            _claimed = new HashSet<string>(StringComparer.Ordinal);
            _types = null;
            _generation++;
            _confirmPending = false;
            _ownNoticeUntil = DateTimeOffset.MinValue;
            _heldChanges = 0;
            _heldTypes = null;
        }
    }

    // under _lock
    private void Detach()
    {
        if (_portal == null) return;
        _portal.SelectionOwnerChanged -= OnOwnerChanged;
        _portal.SelectionTransfer -= OnTransfer;
        _portal = null;
    }

    public async ValueTask DisposeAsync()
    {
        IClipboardSync? fallback;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            Detach();
            fallback = _fallback;
        }
        if (_session != null) _session.SessionLost -= OnSessionLost;
        await _stop.CancelAsync();
        _stop.Dispose();
        if (fallback is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
        else if (fallback is IDisposable disposable) disposable.Dispose();
    }

    // the portal's clipboard, or the sync every call goes to in its place
    private sealed record Backend(IPortalClipboard? Portal, IClipboardSync? Fallback);

    private sealed record GenerationRead(long Generation, ClipboardRead Read);

    // a claim's own copy, what it served, and the count it was claimed at
    private sealed record ClaimedRead(long Count, ClipboardRead Read, Dictionary<string, byte[]> Served);
}
