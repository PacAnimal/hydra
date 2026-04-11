using System.Runtime.InteropServices;
using System.Text;

namespace Hydra.Platform.Linux;

public sealed class XorgClipboardSync : IClipboardSync, IDisposable
{
    // max bytes to send/receive in a single XChangeProperty (INCR threshold)
    private const int MaxPropertyBytes = 256 * 1024;
    private const int ClipboardTimeoutMs = 2000;

    private readonly nint _display;
    private readonly nint _window;

    // atoms
    private readonly nint _atomClipboard;
    private readonly nint _atomTargets;
    private readonly nint _atomUtf8String;
    private readonly nint _atomIncr;
    private readonly nint _atomImagePng;
    private readonly nint _atomHydraClipboard;  // property for CLIPBOARD ConvertSelection responses
    private readonly nint _atomHydraPrimary;    // property for PRIMARY ConvertSelection responses
    private readonly nint _atomHydraImageClip;  // property for image ConvertSelection responses

    // owned clipboard state (written by Set*, read by event loop thread)
    private readonly Lock _dataLock = new();
    private string? _ownedText;
    private string? _ownedPrimaryText;
    private byte[]? _ownedImagePng;
    private string? _lastSetText;
    private string? _lastSetPrimaryText;
    private ulong? _lastSetImageHash;

    // GetText/GetImagePng synchronization — only one read in flight at a time
    private readonly Lock _getLock = new();
    private volatile ManualResetEventSlim? _getSignal;
    private volatile byte[]? _getResult;
    private volatile bool _fallbackToString; // true when UTF8_STRING failed, retrying with STRING
    private volatile nint _activeReadSelection; // selection atom being read
    private volatile nint _activeReadProperty;  // property atom being read
    private volatile nint _activeReadTarget;    // target atom being read

    // INCR receive: accumulates chunks for a large clipboard read
    private MemoryStream? _incrReceiveBuffer;

    // INCR send: tracks in-progress large clipboard sends, keyed by requestor window
    private readonly Dictionary<nint, IncrSendState> _incrSends = [];

    private Thread? _eventThread;
    private volatile bool _running;

    public XorgClipboardSync()
    {
        _ = NativeMethods.XInitThreads();
        _display = NativeMethods.XOpenDisplay(null);
        if (_display == nint.Zero)
            throw new InvalidOperationException("Failed to open X11 display for clipboard");

        var root = NativeMethods.XDefaultRootWindow(_display);
        _window = NativeMethods.XCreateSimpleWindow(_display, root, 0, 0, 1, 1, 0, nint.Zero, nint.Zero);

        // need PropertyChange events for INCR receive
        _ = NativeMethods.XSelectInput(_display, _window, NativeMethods.PropertyChangeMask);

        _atomClipboard = NativeMethods.XInternAtom(_display, "CLIPBOARD", false);
        _atomTargets = NativeMethods.XInternAtom(_display, "TARGETS", false);
        _atomUtf8String = NativeMethods.XInternAtom(_display, "UTF8_STRING", false);
        _atomIncr = NativeMethods.XInternAtom(_display, "INCR", false);
        _atomImagePng = NativeMethods.XInternAtom(_display, "image/png", false);
        _atomHydraClipboard = NativeMethods.XInternAtom(_display, "HYDRA_CLIPBOARD", false);
        _atomHydraPrimary = NativeMethods.XInternAtom(_display, "HYDRA_PRIMARY", false);
        _atomHydraImageClip = NativeMethods.XInternAtom(_display, "HYDRA_IMAGE_CLIP", false);

        _running = true;
        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "HydraClipboardEventLoop" };
        _eventThread.Start();
    }

    public string? GetText()
    {
        // fast path: we own the clipboard — return null to prevent re-syncing our own write
        if (NativeMethods.XGetSelectionOwner(_display, _atomClipboard) == _window)
            return null;
        var bytes = ReadSelectionBytes(_atomClipboard, _atomHydraClipboard, _atomUtf8String);
        if (bytes == null) return null;
        var text = Encoding.UTF8.GetString(bytes);
        return text == _lastSetText ? null : text;
    }

    public string? GetPrimaryText()
    {
        if (NativeMethods.XGetSelectionOwner(_display, NativeMethods.XA_PRIMARY) == _window)
            return null;
        var bytes = ReadSelectionBytes(NativeMethods.XA_PRIMARY, _atomHydraPrimary, _atomUtf8String);
        if (bytes == null) return null;
        var text = Encoding.UTF8.GetString(bytes);
        return text == _lastSetPrimaryText ? null : text;
    }

    public byte[]? GetImagePng()
    {
        if (NativeMethods.XGetSelectionOwner(_display, _atomClipboard) == _window)
            return null;
        var bytes = ReadSelectionBytes(_atomClipboard, _atomHydraImageClip, _atomImagePng);
        if (bytes == null) return null;
        if (_lastSetImageHash.HasValue && ClipboardUtils.QuickHash(bytes) == _lastSetImageHash.Value)
            return null;
        return bytes;
    }

    public void SetText(string text)
    {
        _lastSetText = text;
        lock (_dataLock)
        {
            _ownedText = text;
            _ownedImagePng = null; // replacing clipboard content
        }
        _ = NativeMethods.XSetSelectionOwner(_display, _atomClipboard, _window, NativeMethods.CurrentTime);
        _ = NativeMethods.XFlush(_display);
    }

    public void SetPrimaryText(string text)
    {
        _lastSetPrimaryText = text;
        lock (_dataLock)
            _ownedPrimaryText = text;
        _ = NativeMethods.XSetSelectionOwner(_display, NativeMethods.XA_PRIMARY, _window, NativeMethods.CurrentTime);
        _ = NativeMethods.XFlush(_display);
    }

    public void SetImagePng(byte[] pngData)
    {
        _lastSetImageHash = ClipboardUtils.QuickHash(pngData);
        lock (_dataLock)
        {
            _ownedImagePng = pngData;
            _ownedText = null; // replacing clipboard content
        }
        _ = NativeMethods.XSetSelectionOwner(_display, _atomClipboard, _window, NativeMethods.CurrentTime);
        _ = NativeMethods.XFlush(_display);
    }

    public void SetClipboard(string? text, string? primaryText, byte[]? imagePng)
    {
        if (text == null && primaryText == null && imagePng == null) return;

        if (text != null) _lastSetText = text;
        if (imagePng != null) _lastSetImageHash = ClipboardUtils.QuickHash(imagePng);

        lock (_dataLock)
        {
            if (text != null) _ownedText = text;
            if (imagePng != null) _ownedImagePng = imagePng;
            // only clear the other field if we're setting something exclusive
            if (text != null && imagePng == null) _ownedImagePng = null;
            if (imagePng != null && text == null) _ownedText = null;
        }

        _ = NativeMethods.XSetSelectionOwner(_display, _atomClipboard, _window, NativeMethods.CurrentTime);
        _ = NativeMethods.XFlush(_display);

        if (primaryText != null)
        {
            _lastSetPrimaryText = primaryText;
            lock (_dataLock)
                _ownedPrimaryText = primaryText;
            _ = NativeMethods.XSetSelectionOwner(_display, NativeMethods.XA_PRIMARY, _window, NativeMethods.CurrentTime);
            _ = NativeMethods.XFlush(_display);
        }
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
                NativeMethods.poll(ref pfd, 1, 100);
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
                HandleSelectionNotify(ref ev);
                break;
            case NativeMethods.SelectionClear:
                {
                    var sel = ev.SelectionClearSelection;
                    lock (_dataLock)
                    {
                        if (sel == _atomClipboard) { _ownedText = null; _ownedImagePng = null; }
                        else if (sel == NativeMethods.XA_PRIMARY) _ownedPrimaryText = null;
                    }
                    break;
                }
            case NativeMethods.PropertyNotify:
                HandlePropertyNotify(ref ev);
                break;
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
        if (target == _atomTargets)
        {
            var atoms = new List<nint> { _atomTargets, _atomUtf8String, NativeMethods.XA_STRING };
            bool hasImage;
            lock (_dataLock)
                hasImage = selection == _atomClipboard && _ownedImagePng != null;
            if (hasImage) atoms.Add(_atomImagePng);

            _ = NativeMethods.XChangeProperty(_display, requestor, property,
                NativeMethods.XA_ATOM, 32, NativeMethods.PropModeReplace, atoms.ToArray(), atoms.Count);
            return property;
        }

        if (target == _atomImagePng)
        {
            byte[]? img;
            lock (_dataLock)
                img = selection == _atomClipboard ? _ownedImagePng : null;
            if (img == null) return nint.Zero;

            if (img.Length <= MaxPropertyBytes)
            {
                _ = NativeMethods.XChangeProperty(_display, requestor, property,
                    _atomImagePng, 8, NativeMethods.PropModeReplace, img, img.Length);
                return property;
            }
            return StartIncrSend(requestor, property, _atomImagePng, img);
        }

        if (target != _atomUtf8String && target != NativeMethods.XA_STRING)
            return nint.Zero;

        string? text;
        lock (_dataLock)
        {
            text = selection == NativeMethods.XA_PRIMARY ? _ownedPrimaryText : _ownedText;
        }
        if (text == null) return nint.Zero;

        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= MaxPropertyBytes)
        {
            _ = NativeMethods.XChangeProperty(_display, requestor, property,
                target, 8, NativeMethods.PropModeReplace, bytes, bytes.Length);
            return property;
        }

        return StartIncrSend(requestor, property, target, bytes);
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

    // returns raw bytes for the selection, or null on failure/timeout
    private byte[]? ReadSelectionBytes(nint selectionAtom, nint propertyAtom, nint targetAtom)
    {
        lock (_getLock)
        {
            _getResult = null;
            _fallbackToString = false;
            _activeReadSelection = selectionAtom;
            _activeReadProperty = propertyAtom;
            _activeReadTarget = targetAtom;
            using var signal = new ManualResetEventSlim(false);
            _getSignal = signal;
            try
            {
                _ = NativeMethods.XConvertSelection(_display, selectionAtom, targetAtom,
                    propertyAtom, _window, NativeMethods.CurrentTime);
                _ = NativeMethods.XFlush(_display);

                if (!signal.Wait(ClipboardTimeoutMs))
                    return null;

                return _getResult;
            }
            finally
            {
                _getSignal = null;
                _activeReadSelection = nint.Zero;
                _activeReadProperty = nint.Zero;
                _activeReadTarget = nint.Zero;
            }
        }
    }

    private void HandleSelectionNotify(ref XEvent ev)
    {
        var activeSelection = _activeReadSelection;
        if (activeSelection == nint.Zero || ev.SelectionNotifySelection != activeSelection)
            return;

        var propertyAtom = _activeReadProperty;
        var targetAtom = _activeReadTarget;

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
        var rc = NativeMethods.XGetWindowProperty(_display, _window, propertyAtom,
            nint.Zero, 1_000_000 / 4, true, NativeMethods.AnyPropertyType,
            out var actualType, out var format, out var nitems, out _, out var prop);

        if (rc != 0)
        {
            _getResult = null;
            SignalGet();
            return;
        }

        if (actualType == _atomIncr)
        {
            // large transfer — chunks will arrive via PropertyNotify
            if (prop != nint.Zero) _ = NativeMethods.XFree(prop);
            _incrReceiveBuffer = new MemoryStream();
            return;
        }

        byte[]? result = null;
        if (nitems != nint.Zero && prop != nint.Zero)
        {
            var byteCount = (int)nitems * (format / 8);
            result = new byte[byteCount];
            Marshal.Copy(prop, result, 0, byteCount);
        }
        if (prop != nint.Zero) _ = NativeMethods.XFree(prop);

        _getResult = result;
        SignalGet();
    }

    private void HandlePropertyNotify(ref XEvent ev)
    {
        var window = ev.EventWindow;
        var atom = ev.PropertyNotifyAtom;
        var state = ev.PropertyNotifyState;

        // INCR receive: owner wrote a new chunk to our window's property
        if (_incrReceiveBuffer != null && window == _window
            && atom == _activeReadProperty && state == NativeMethods.PropertyNewValue)
        {
            HandleIncrReceiveChunk();
            return;
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
        // read timed out — discard remaining chunks and clean up
        if (_getSignal == null)
        {
            _incrReceiveBuffer?.Dispose();
            _incrReceiveBuffer = null;
            return;
        }

        var propertyAtom = _activeReadProperty;
        var rc = NativeMethods.XGetWindowProperty(_display, _window, propertyAtom,
            nint.Zero, MaxPropertyBytes / 4, true, NativeMethods.AnyPropertyType,
            out _, out var format, out var nitems, out _, out var prop);

        if (rc != 0)
        {
            if (prop != nint.Zero) _ = NativeMethods.XFree(prop);
            FinishIncrReceive(null);
            return;
        }

        if (nitems == nint.Zero)
        {
            // zero-length chunk = end of transfer
            if (prop != nint.Zero) _ = NativeMethods.XFree(prop);
            FinishIncrReceive(_incrReceiveBuffer!.ToArray());
            return;
        }

        var byteCount = (int)nitems * (format / 8);
        var chunk = new byte[byteCount];
        Marshal.Copy(prop, chunk, 0, byteCount);
        _ = NativeMethods.XFree(prop);
        _incrReceiveBuffer!.Write(chunk);
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

    private sealed class IncrSendState
    {
        public required byte[] Data;
        public required int Offset;
        public required nint Property;
        public required nint TargetAtom;
    }
}
