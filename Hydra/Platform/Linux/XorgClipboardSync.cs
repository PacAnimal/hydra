using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Hydra.Platform.Linux;

public sealed class XorgClipboardSync : IClipboardSync, IDisposable
{
    // max bytes to send/receive in a single XChangeProperty (INCR threshold)
    private const int MaxPropertyBytes = 256 * 1024;
    // how much of a property one read takes, in 32-bit units; owners send more than this by INCR
    private const int ReadPropertyLongs = 1_000_000 / 4;
    private const int ClipboardTimeoutMs = 2000;
    // the longest an INCR transfer that keeps sending chunks may take
    private const int ClipboardTotalTimeoutMs = 30_000;
    // the properties reads take turns with after one gives up, so atoms never pile up on the server
    private const int ReadProperties = 16;

    private readonly nint _display;
    private readonly nint _window;

    // atoms
    private readonly nint _atomClipboard;
    private readonly nint _atomTargets;
    private readonly nint _atomTimestamp;
    private readonly nint _atomUtf8String;
    private readonly nint _atomIncr;
    private readonly nint _atomImagePng;
    private readonly nint _atomTextHtml;
    private readonly nint _atomPasswordHint;    // KDE's mark of a password manager's copy

    // what the password hint holds for a concealed copy
    private static readonly byte[] PasswordHintSecret = "secret"u8.ToArray();

    // owned clipboard state (written by Set*, read by event loop thread). CLIPBOARD's ownership and change counter are
    // checked and moved under it too, so a clear for an earlier loss never nulls new data and a selection another app
    // has just taken is never answered with ours
    private readonly Lock _dataLock = new();
    private ClipboardSnapshot? _ownedClipboard;
    private string? _ownedPrimaryText;
    // CLIPBOARD's change counter: moved by each of Hydra's own writes and each other client's taking of it, an owner
    // taking it again (an identical re-copy) among them, which XFixes reports. Set once XFixes is known
    private readonly OwnerCounter _counter;
    // the count Hydra's own last taking of CLIPBOARD left, null before any
    private long? _wroteAt;
    // the newest owner notice counted or passed over: when its owner took CLIPBOARD, and who, so none counts twice; two
    // clients may take it within one server millisecond
    private uint? _lastTaken;
    private nint _lastTaker;
    // XFixes' first event number, -1 without the extension, when only owner changes seen here move the counter
    private readonly int _fixesEventBase = -1;

    // ReadAll synchronization — only one read in flight at a time
    private readonly Lock _getLock = new();
    // the read's state, shared by the reader and the event loop
    private readonly Lock _readLock = new();
    private volatile ManualResetEventSlim? _getSignal;
    private volatile byte[]? _getResult;
    private volatile bool _getFailed; // true when the answer's property could not be read, unlike an owner with no such target
    private volatile bool _fallbackToString; // true when UTF8_STRING failed, retrying with STRING
    private volatile nint _activeReadSelection; // selection atom being read
    private volatile nint _activeReadProperty;  // property atom being read
    private volatile nint _activeReadTarget;    // target atom being read

    // the property reads are answered in; a read that gives up retires it, so a late owner never feeds a later read
    private readonly nint[] _readProperties = new nint[ReadProperties];
    private nint _readProperty;
    private int _retiredProperties;

    // INCR receive: accumulates chunks for a large clipboard read; its start and each chunk counted, so the read's wait re-arms
    private MemoryStream? _incrReceiveBuffer;
    private volatile int _incrChunks;

    // INCR send: tracks in-progress large clipboard sends, keyed by requestor window
    private readonly Dictionary<nint, IncrSendState> _incrSends = [];

    private Thread? _eventThread;
    private volatile bool _running;

    public XorgClipboardSync()
    {
        _display = XlibRuntime.OpenDisplayOrThrow("the clipboard");

        var root = NativeMethods.XDefaultRootWindow(_display);
        _window = NativeMethods.XCreateSimpleWindow(_display, root, 0, 0, 1, 1, 0, nint.Zero, nint.Zero);

        // need PropertyChange events for INCR receive
        _ = NativeMethods.XSelectInput(_display, _window, NativeMethods.PropertyChangeMask);

        _atomClipboard = NativeMethods.XInternAtom(_display, "CLIPBOARD", false);
        _atomTargets = NativeMethods.XInternAtom(_display, "TARGETS", false);
        _atomTimestamp = NativeMethods.XInternAtom(_display, "TIMESTAMP", false);
        _atomUtf8String = NativeMethods.XInternAtom(_display, "UTF8_STRING", false);
        _atomIncr = NativeMethods.XInternAtom(_display, "INCR", false);
        _atomImagePng = NativeMethods.XInternAtom(_display, "image/png", false);
        _atomTextHtml = NativeMethods.XInternAtom(_display, "text/html", false);
        _atomPasswordHint = NativeMethods.XInternAtom(_display, "x-kde-passwordManagerHint", false);
        for (var i = 0; i < ReadProperties; i++)
            _readProperties[i] = NativeMethods.XInternAtom(_display, $"HYDRA_READ_{i}", false);
        _readProperty = _readProperties[0];

        if (NativeMethods.XFixesQueryExtension(_display, out var fixesEventBase, out _))
        {
            _fixesEventBase = fixesEventBase;
            NativeMethods.XFixesSelectSelectionInput(_display, _window, _atomClipboard, NativeMethods.XFixesSetSelectionOwnerNotifyMask);
        }
        _counter = new OwnerCounter(_fixesEventBase >= 0);

        _running = true;
        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "HydraClipboardEventLoop" };
        _eventThread.Start();
    }

    internal bool OwnsPrimary => NativeMethods.XGetSelectionOwner(_display, NativeMethods.XA_PRIMARY) == _window;

    // never echo-suppressed: the selection travels as it stands, even when Hydra set it
    public string? GetPrimaryText()
    {
        lock (_dataLock)
        {
            if (OwnsPrimary && _ownedPrimaryText != null) return _ownedPrimaryText;
        }
        try
        {
            return ReadSelection(NativeMethods.XA_PRIMARY, _atomUtf8String)?.DecodeText();
        }
        catch (ClipboardUnreadableException)
        {
            return null; // a selection that could not be read is none, as the interface has it
        }
    }

    // CLIPBOARD as another client holds it, every target it lists against one owner and timestamp; nothing while we own
    // it, so our own write is never synced back, and nothing echo-filtered: what another client holds is its copy, even
    // when it equals Hydra's last write. A copy made during the read is read again, once
    public ClipboardRead ReadAll()
    {
        lock (_getLock)
            return ReadAllOnce() ?? ReadAllOnce() ?? throw new ClipboardUnreadableException("the clipboard changed while it was read, twice");
    }

    // null when the clipboard changed during the read
    private ClipboardRead? ReadAllOnce()
    {
        var owner = NativeMethods.XGetSelectionOwner(_display, _atomClipboard);
        if (owner == _window || owner == nint.Zero) return ClipboardRead.Empty;
        // an owner that refuses TARGETS is asked for its text alone, UTF8_STRING then STRING
        if (ReadSelection(_atomClipboard, _atomTargets) is not { } listed)
        {
            var plain = ReadSelection(_atomClipboard, _atomUtf8String);
            if (NativeMethods.XGetSelectionOwner(_display, _atomClipboard) != owner) return null;
            return new ClipboardRead(plain?.DecodeText(), null, null, null, false);
        }
        var targets = Atoms(listed.Data);
        var stamped = targets.Contains(_atomTimestamp);
        var stamp = stamped ? ReadSelection(_atomClipboard, _atomTimestamp)?.Data : null;
        var text = targets.Contains(_atomUtf8String) ? ReadSelection(_atomClipboard, _atomUtf8String)
            : targets.Contains(NativeMethods.XA_STRING) ? ReadSelection(_atomClipboard, NativeMethods.XA_STRING) : null;
        var image = targets.Contains(_atomImagePng) ? ReadSelection(_atomClipboard, _atomImagePng)?.Data : null;
        var html = targets.Contains(_atomTextHtml) ? ReadSelection(_atomClipboard, _atomTextHtml)?.DecodeText() : null;
        var hint = targets.Contains(_atomPasswordHint) ? ReadSelection(_atomClipboard, _atomPasswordHint)?.Data : null;
        if (NativeMethods.XGetSelectionOwner(_display, _atomClipboard) != owner) return null;
        if (stamped && !stamp.AsSpan().SequenceEqual(ReadSelection(_atomClipboard, _atomTimestamp)?.Data)) return null;

        return new ClipboardRead(text?.DecodeText(), image, html, null, hint.AsSpan().SequenceEqual(PasswordHintSecret));
    }

    // a 32-bit property's items, which Xlib hands back as longs
    private static nint[] Atoms(byte[] data) => MemoryMarshal.Cast<byte, nint>(data).ToArray();

    // whether CLIPBOARD is still Hydra's write: SelectionClear forgets it once another client takes it
    public bool CanUseEchoFallback()
    {
        lock (_dataLock)
            return _ownedClipboard != null;
    }

    // X11 keeps no counter, so this one moves on Hydra's own writes, on XFixes' owner notices, and on an owner change
    // seen here (a read already sees the new owner before its notice is handled); without XFixes, on SelectionClear too,
    // when a copy by the owner it already was goes unseen
    public long? ChangeCount()
    {
        lock (_dataLock)
        {
            NoteOwner();
            return _counter.Count;
        }
    }

    // the count Hydra's own last write or clear left, not the counter as it stands
    public long? WroteAt()
    {
        lock (_dataLock)
            return _wroteAt;
    }

    // under _dataLock: CLIPBOARD's owner as it stands, counted once when it changed, however it was seen
    private void NoteOwner() => _counter.Seen(NativeMethods.XGetSelectionOwner(_display, _atomClipboard));

    public bool SetPrimaryText(string text)
    {
        // taking PRIMARY back with the text it already holds would clear its owner's highlight
        if (GetPrimaryText() != text) Own(NativeMethods.XA_PRIMARY, () => _ownedPrimaryText = text);
        return true;
    }

    // the owner serves every format it holds, so none is refused
    public ClipboardSnapshot SetClipboard(ClipboardSnapshot contents)
    {
        // the snapshot is the authoritative new clipboard — replace all owned representations
        Own(_atomClipboard, () => _ownedClipboard = contents);

        if (contents.PrimaryText is { } primaryText) SetPrimaryText(primaryText);
        return contents;
    }

    private void Own(nint selection, Action setData)
    {
        lock (_dataLock)
        {
            setData();
            _ = NativeMethods.XSetSelectionOwner(_display, selection, _window, NativeMethods.CurrentTime);
            if (selection == _atomClipboard)
            {
                _wroteAt = _counter.Own(_window);
            }
        }
        _ = NativeMethods.XFlush(_display);
    }

    public void Dispose()
    {
        _running = false;
        SignalGet(); // unblock any waiting read
        _eventThread?.Join(TimeSpan.FromSeconds(2));
        _eventThread = null;

        _incrReceiveBuffer?.Dispose();
        _incrReceiveBuffer = null;
        _incrSends.Clear();

        if (_window != nint.Zero) _ = NativeMethods.XDestroyWindow(_display, _window);
        if (_display != nint.Zero) _ = NativeMethods.XCloseDisplay(_display);
    }

    // -- event loop --

    private void EventLoop()
    {
        var xFd = NativeMethods.XConnectionNumber(_display);
        var pfd = new PollFd { Fd = xFd, Events = NativeMethods.POLLIN };

        while (_running)
        {
            if (NativeMethods.XPending(_display) > 0)
            {
                _ = NativeMethods.XNextEvent(_display, out var ev);
                HandleEvent(ref ev);
            }
            else
                NativeMethods.Poll(ref pfd, 1, 100);
        }
    }

    private void HandleEvent(ref XEvent ev)
    {
        switch (ev.Type)
        {
            case NativeMethods.SelectionRequest:
                HandleSelectionRequest(ref ev);
                break;
            case NativeMethods.SelectionNotify:
                lock (_readLock)
                    HandleSelectionNotify(ref ev);
                break;
            case NativeMethods.SelectionClear:
                {
                    var sel = ev.SelectionClearSelection;
                    lock (_dataLock)
                    {
                        if (NativeMethods.XGetSelectionOwner(_display, sel) == _window) break;
                        if (sel == _atomClipboard)
                        {
                            _ownedClipboard = null;
                            if (_fixesEventBase < 0) NoteOwner();
                        }
                        else if (sel == NativeMethods.XA_PRIMARY) _ownedPrimaryText = null;
                    }
                    break;
                }
            case NativeMethods.PropertyNotify:
                HandlePropertyNotify(ref ev);
                break;
            case var fixes when _fixesEventBase >= 0 && fixes == _fixesEventBase + NativeMethods.XFixesSelectionNotify:
                OwnerChanged(ref ev);
                break;
        }
    }

    // another client took CLIPBOARD, perhaps again with what it already held: a copy, counted once by when it was
    // taken (OwnerCounter: Hydra's own taking was counted by Own, a notice arriving after Hydra took it back is older
    // than that write, and an owner ChangeCount already counted spends its credit)
    private void OwnerChanged(ref XEvent ev)
    {
        if (ev.FixesSelectionSelection != _atomClipboard) return;
        var taken = (uint)ev.FixesSelectionTimestamp;
        lock (_dataLock)
        {
            if (_lastTaken is { } last && ((int)(taken - last) < 0 || (taken == last && ev.FixesSelectionOwner == _lastTaker))) return;
            _lastTaken = taken;
            _lastTaker = ev.FixesSelectionOwner;
            _counter.Notice(ev.FixesSelectionOwner, NativeMethods.XGetSelectionOwner(_display, _atomClipboard), _window);
        }
    }

    // -- serving data to other apps (we own CLIPBOARD or PRIMARY) --

    private void HandleSelectionRequest(ref XEvent ev)
    {
        var requestor = ev.SelectionRequestRequestor;
        var selection = ev.SelectionRequestSelection;

        // ICCCM: if property is None, use target atom as property name
        var property = ev.SelectionRequestProperty == nint.Zero
            ? ev.SelectionRequestTarget
            : ev.SelectionRequestProperty;

        var resp = default(XEvent);
        resp.Type = NativeMethods.SelectionNotify;
        resp.SendEvent = 1;
        resp.EventDisplay = _display;
        resp.SelectionNotifyRequestor = requestor;
        resp.SelectionNotifySelection = selection;
        resp.SelectionNotifyTarget = ev.SelectionRequestTarget;
        resp.SelectionNotifyProperty = nint.Zero; // failure default
        resp.SelectionNotifyTime = ev.SelectionRequestTime;

        if (selection == _atomClipboard || selection == NativeMethods.XA_PRIMARY)
            resp.SelectionNotifyProperty = WriteTargetToProperty(selection, requestor, ev.SelectionRequestTarget, property);

        _ = NativeMethods.XSendEvent(_display, requestor, false, nint.Zero, ref resp);
        _ = NativeMethods.XFlush(_display);
    }

    private nint WriteTargetToProperty(nint selection, nint requestor, nint target, nint property)
    {
        ClipboardSnapshot? clipboard;
        string? text;
        lock (_dataLock)
        {
            clipboard = selection == _atomClipboard ? _ownedClipboard : null;
            text = selection == NativeMethods.XA_PRIMARY ? _ownedPrimaryText : clipboard?.Text;
        }

        // the placeholder carries the hint too, so clipboard managers never keep it
        var hinted = clipboard is { Concealed: true } or { IsClear: true };

        if (target == _atomTargets)
        {
            // TARGETS always, and only what there is to serve beside it
            var atoms = new List<nint> { _atomTargets };
            if (text != null) atoms.AddRange([_atomUtf8String, NativeMethods.XA_STRING]);
            if (clipboard?.ImagePng != null) atoms.Add(_atomImagePng);
            if (clipboard?.Html != null) atoms.Add(_atomTextHtml);
            if (hinted) atoms.Add(_atomPasswordHint);

            _ = NativeMethods.XChangeProperty(_display, requestor, property,
                NativeMethods.XA_ATOM, 32, NativeMethods.PropModeReplace, atoms.ToArray(), atoms.Count);
            return property;
        }

        if (target == _atomPasswordHint)
            return hinted ? ServeBytes(requestor, property, _atomPasswordHint, PasswordHintSecret) : nint.Zero;

        if (target == _atomImagePng)
            return clipboard?.ImagePng is { } image ? ServeBytes(requestor, property, _atomImagePng, image) : nint.Zero;

        if (target == _atomTextHtml)
            return clipboard?.Html is { } html ? ServeBytes(requestor, property, _atomTextHtml, Encoding.UTF8.GetBytes(html)) : nint.Zero;

        if ((target != _atomUtf8String && target != NativeMethods.XA_STRING) || text == null)
            return nint.Zero;

        // UTF8_STRING carries UTF-8; the legacy STRING target is ISO-8859-1 (Latin-1) per ICCCM
        var bytes = target == NativeMethods.XA_STRING
            ? Encoding.Latin1.GetBytes(text)
            : Encoding.UTF8.GetBytes(text);
        return ServeBytes(requestor, property, target, bytes);
    }

    // in one property when it fits, else by INCR
    private nint ServeBytes(nint requestor, nint property, nint type, byte[] bytes)
    {
        if (bytes.Length > MaxPropertyBytes) return StartIncrSend(requestor, property, type, bytes);
        _ = NativeMethods.XChangeProperty(_display, requestor, property, type, 8, NativeMethods.PropModeReplace, bytes, bytes.Length);
        return property;
    }

    private nint StartIncrSend(nint requestor, nint property, nint targetAtom, byte[] data)
    {
        _ = NativeMethods.XSelectInput(_display, requestor, NativeMethods.PropertyChangeMask);

        var sizeAtom = new[] { (nint)data.Length };
        _ = NativeMethods.XChangeProperty(_display, requestor, property,
            _atomIncr, 32, NativeMethods.PropModeReplace, sizeAtom, 1);

        _incrSends[requestor] = new IncrSendState { Data = data, Offset = 0, Property = property, TargetAtom = targetAtom };
        return property;
    }

    private void HandleIncrSendChunk(nint requestor, IncrSendState send)
    {
        var remaining = send.Data.Length - send.Offset;
        if (remaining <= 0)
        {
            // signal end of transfer with a zero-length property
            _ = NativeMethods.XChangeProperty(_display, requestor, send.Property,
                send.TargetAtom, 8, NativeMethods.PropModeReplace, Array.Empty<byte>(), 0);
            _ = NativeMethods.XSelectInput(_display, requestor, nint.Zero);
            _incrSends.Remove(requestor);
            return;
        }

        var chunkSize = Math.Min(remaining, MaxPropertyBytes);
        var chunk = new byte[chunkSize];
        Buffer.BlockCopy(send.Data, send.Offset, chunk, 0, chunkSize);
        _ = NativeMethods.XChangeProperty(_display, requestor, send.Property,
            send.TargetAtom, 8, NativeMethods.PropModeReplace, chunk, chunkSize);
        send.Offset += chunkSize;
    }

    // -- reading data from another app --

    // returns the selection bytes plus whether they came from the STRING fallback, or null when the owner has no such
    // target; an owner that does not answer in time, or an answer that cannot be read, leaves it unreadable
    private SelectionRead? ReadSelection(nint selectionAtom, nint targetAtom)
    {
        lock (_getLock)
        {
            using var signal = new ManualResetEventSlim(false);
            nint propertyAtom;
            lock (_readLock)
            {
                propertyAtom = _readProperty;
                _getResult = null;
                _getFailed = false;
                _fallbackToString = false;
                _activeReadSelection = selectionAtom;
                _activeReadProperty = propertyAtom;
                _activeReadTarget = targetAtom;
                _getSignal = signal;
            }
            var answered = false;
            try
            {
                _ = NativeMethods.XConvertSelection(_display, selectionAtom, targetAtom,
                    propertyAtom, _window, NativeMethods.CurrentTime);
                _ = NativeMethods.XFlush(_display);

                answered = AwaitAnswer(signal);
                if (!answered)
                    throw new ClipboardUnreadableException($"the selection's owner did not answer within {ClipboardTimeoutMs} ms");
                if (_getFailed) throw new ClipboardUnreadableException("the selection's answer could not be read");
                return _getResult is { } data ? new SelectionRead(data, _fallbackToString || targetAtom == NativeMethods.XA_STRING) : null;
            }
            finally
            {
                lock (_readLock)
                {
                    _getSignal = null;
                    _activeReadSelection = nint.Zero;
                    _activeReadProperty = nint.Zero;
                    _activeReadTarget = nint.Zero;
                    // a read given up on, perhaps mid-INCR: its transfer is dropped, and its property retired so a late
                    // answer or chunk never feeds or re-arms a later read
                    if (!answered) RetireReadProperty();
                }
            }
        }
    }

    // under _readLock
    private void RetireReadProperty()
    {
        _incrReceiveBuffer?.Dispose();
        _incrReceiveBuffer = null;
        _retiredProperties = (_retiredProperties + 1) % ReadProperties;
        _readProperty = _readProperties[_retiredProperties];
    }

    // whether the answer came: the wait starts over with each INCR chunk, so a slow transfer that still progresses is
    // never given up on, though none outlasts ClipboardTotalTimeoutMs
    private bool AwaitAnswer(ManualResetEventSlim signal)
    {
        var total = Stopwatch.StartNew();
        while (true)
        {
            var chunks = _incrChunks;
            var left = ClipboardTotalTimeoutMs - (int)total.ElapsedMilliseconds;
            if (left <= 0) return false;
            if (signal.Wait(Math.Min(ClipboardTimeoutMs, left))) return true;
            if (_incrChunks == chunks) return false;
        }
    }

    private void HandleSelectionNotify(ref XEvent ev)
    {
        var activeSelection = _activeReadSelection;
        var propertyAtom = _activeReadProperty;
        var targetAtom = _activeReadTarget;

        // a late answer to a read that already timed out names another target or property
        var expectedTarget = _fallbackToString ? NativeMethods.XA_STRING : targetAtom;
        if (activeSelection == nint.Zero || ev.SelectionNotifySelection != activeSelection || ev.SelectionNotifyTarget != expectedTarget
            || (ev.SelectionNotifyProperty != nint.Zero && ev.SelectionNotifyProperty != propertyAtom))
            return;

        if (ev.SelectionNotifyProperty == nint.Zero)
        {
            // conversion failed — try STRING fallback once (text only)
            if (_fallbackToString || targetAtom != _atomUtf8String)
            {
                _getResult = null;
                SignalGet();
            }
            else
            {
                _fallbackToString = true;
                _ = NativeMethods.XConvertSelection(_display, activeSelection, NativeMethods.XA_STRING,
                    propertyAtom, _window, NativeMethods.CurrentTime);
                _ = NativeMethods.XFlush(_display);
            }
            return;
        }

        // read the property — delete=true to signal the owner we consumed it (required for INCR)
        if (!ReadProperty(propertyAtom, out var actualType, out var data))
        {
            _getResult = null;
            _getFailed = true;
            SignalGet();
            return;
        }

        if (actualType == _atomIncr)
        {
            // large transfer — chunks will arrive via PropertyNotify
            _incrReceiveBuffer = new MemoryStream();
            Interlocked.Increment(ref _incrChunks);
            return;
        }

        _getResult = data;
        SignalGet();
    }

    // the whole of a property, ReadPropertyLongs at a time, deleted once read to its end; data null when it held nothing,
    // false when Xlib could not read it
    private bool ReadProperty(nint propertyAtom, out nint actualType, out byte[]? data)
    {
        using var whole = new MemoryStream();
        for (nint offset = 0; ; offset += ReadPropertyLongs)
        {
            var rc = NativeMethods.XGetWindowProperty(_display, _window, propertyAtom,
                offset, ReadPropertyLongs, true, NativeMethods.AnyPropertyType,
                out actualType, out var format, out var nitems, out var bytesAfter, out var prop);
            if (rc != 0)
            {
                if (prop != nint.Zero) _ = NativeMethods.XFree(prop);
                data = null;
                return false;
            }
            if (TakeProperty(prop, nitems, format) is { } piece) whole.Write(piece);
            // bytes after this read: it took a whole ReadPropertyLongs, so the next starts there
            if (bytesAfter == nint.Zero)
            {
                data = whole.Length == 0 ? null : whole.ToArray();
                return true;
            }
        }
    }

    // copies a property XGetWindowProperty returned and frees it; null when it held nothing
    private static byte[]? TakeProperty(nint prop, nint nitems, int format)
    {
        if (prop == nint.Zero) return null;
        byte[]? data = null;
        if (nitems != nint.Zero)
        {
            // Xlib hands back 32-bit items as longs
            data = new byte[(int)nitems * (format == 32 ? nint.Size : format / 8)];
            Marshal.Copy(prop, data, 0, data.Length);
        }
        _ = NativeMethods.XFree(prop);
        return data;
    }

    private void HandlePropertyNotify(ref XEvent ev)
    {
        var window = ev.EventWindow;
        var atom = ev.PropertyNotifyAtom;
        var state = ev.PropertyNotifyState;

        // INCR receive: owner wrote a new chunk to the property of the read still waiting for it
        lock (_readLock)
        {
            if (_incrReceiveBuffer != null && _getSignal != null && window == _window
                && atom == _activeReadProperty && state == NativeMethods.PropertyNewValue)
            {
                HandleIncrReceiveChunk();
                return;
            }
        }

        // INCR send: requestor deleted its property, ready for next chunk
        if (state == NativeMethods.PropertyDelete
            && _incrSends.TryGetValue(window, out var send)
            && atom == send.Property)
        {
            HandleIncrSendChunk(window, send);
        }
    }

    private void HandleIncrReceiveChunk()
    {
        var propertyAtom = _activeReadProperty;
        if (!ReadProperty(propertyAtom, out _, out var chunk))
        {
            _getFailed = true;
            FinishIncrReceive(null);
            return;
        }

        // a zero-length chunk ends the transfer
        if (chunk != null)
        {
            _incrReceiveBuffer!.Write(chunk);
            Interlocked.Increment(ref _incrChunks);
        }
        else FinishIncrReceive(_incrReceiveBuffer!.ToArray());
    }

    private void FinishIncrReceive(byte[]? result)
    {
        _incrReceiveBuffer?.Dispose();
        _incrReceiveBuffer = null;
        _getResult = result;
        SignalGet();
    }

    // safe to call from event loop — the MRE may already be disposed if read timed out
    private void SignalGet()
    {
        try { _getSignal?.Set(); }
        catch (ObjectDisposedException) { }
    }

    // bytes read from a selection, plus whether they arrived via the Latin-1 STRING fallback
    private sealed record SelectionRead(byte[] Data, bool Latin1)
    {
        public string DecodeText() => (Latin1 ? Encoding.Latin1 : Encoding.UTF8).GetString(Data);
    }

    private sealed class IncrSendState
    {
        public required byte[] Data;
        public required int Offset;
        public required nint Property;
        public required nint TargetAtom;
    }
}
