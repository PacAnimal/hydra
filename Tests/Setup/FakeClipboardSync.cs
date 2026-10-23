using Hydra.Platform;

namespace Tests.Setup;

public sealed class FakeClipboardSync : IClipboardSync
{
    public string? Text { get; private set; }
    public string? PrimaryText { get; private set; }
    public byte[]? ImagePng { get; private set; }
    public string? Html { get; private set; }
    public byte[]? Rtf { get; private set; }
    public int ReadCallCount => Volatile.Read(ref _reads);
    private int _reads;
    public int GetPrimaryTextCallCount { get; private set; }
    public int SetClipboardCallCount { get; private set; }
    public int ClearClipboardCallCount { get; private set; }
    public bool HasFileClipboardValue { get; set; }

    // another app taking the clipboard is a copy, which moves the change counter
    public bool CanUseEchoFallbackValue
    {
        get => _canUseEchoFallback;
        set
        {
            if (_canUseEchoFallback && !value) _changes++;
            _canUseEchoFallback = value;
        }
    }
    private bool _canUseEchoFallback = true;

    // the change counter: every user copy and every one of Hydra's writes and clears moves it; unsupported while false
    public bool ChangeCountSupported { get; set; } = true;
    private long _changes;
    public bool WriteFailsValue { get; set; }
    // a clear fails, as with the clipboard held open by another app
    public bool ClearFailsValue { get; set; }
    public bool KeepsSelectionValue { get; set; } = true;
    // the clipboard's reads throw, as for a clipboard held open by another app
    public bool ReadFailsValue { get; set; }
    // the formats a write leaves out, as a clipboard refusing them does
    public HashSet<ClipboardFormat> RefusedFormats { get; } = [];
    // the concealed markers are refused, so a concealed copy is never written
    public bool RefusesMarkersValue { get; set; }
    // the reads see nothing of Hydra's own write, as a platform filters its echo, until the test sets the clipboard
    public bool HidesOwnWriteValue { get; set; }
    private bool _ownWrite;

    // while held, a read parks in ReadAll until released, as against a hung clipboard owner; it returns the text it saw
    // before parking, as a read slow to answer with an older clipboard
    public Gate ReadGate { get; } = new();
    // a read reaching the held gate, before it parks
    public NotificationQueue<string> ReadsParked { get; } = new();

    // the clipboard carries a password manager's markers
    public bool ConcealedValue { get; set; }

    public ClipboardRead ReadAll()
    {
        if (HasFileClipboard()) return ClipboardRead.Files;
        Interlocked.Increment(ref _reads);
        var text = Text;
        if (ReadGate.IsHeld) ReadsParked.Push("parked");
        ReadGate.Passed().GetAwaiter().GetResult();
        if (ReadFailsValue) throw new ClipboardUnreadableException("the fake clipboard is unreadable");
        return new ClipboardRead(Readable(text), Readable(ImagePng), Readable(Html), Readable(Rtf), ConcealedValue);
    }

    private T? Readable<T>(T? value) where T : class => HidesOwnWriteValue && _ownWrite ? null : value;

    public string? GetPrimaryText()
    {
        GetPrimaryTextCallCount++;
        return PrimaryText;
    }

    public bool SetPrimaryText(string text)
    {
        if (!KeepsSelectionValue) return false;
        PrimaryText = text;
        return true;
    }

    public bool HasFileClipboard() => HasFileClipboardValue;

    public bool CanUseEchoFallback() => CanUseEchoFallbackValue;

    public long? ChangeCount()
    {
        OnChangeCount?.Invoke();
        return ChangeCountSupported ? _changes : null;
    }

    // runs as the change counter is read, to see where it is read from or act between the reads
    public Action? OnChangeCount { get; set; }

    // the count Hydra's own last write or clear left, as the platforms report it
    private long? _wroteAt;

    public long? WroteAt() => ChangeCountSupported ? _wroteAt : null;

    // runs as a write ends, failed or not, as a copy landing during it
    public Action? AfterWrite { get; set; }

    public ClipboardSnapshot? SetClipboard(ClipboardSnapshot contents)
    {
        try
        {
            return Write(contents);
        }
        finally
        {
            AfterWrite?.Invoke();
        }
    }

    private ClipboardSnapshot? Write(ClipboardSnapshot contents)
    {
        SetClipboardCallCount++;
        // mirror real implementations: the selection beside it, and every format replaced, a refused one left empty
        if (contents.PrimaryText != null) SetPrimaryText(contents.PrimaryText);
        if (WriteFailsValue) return null;
        _wroteAt = ++_changes;
        // as the platforms do when the markers are refused: the placeholder in place of the copy
        if (contents.Concealed && RefusesMarkersValue)
        {
            Text = null;
            ImagePng = null;
            Html = null;
            Rtf = null;
            ConcealedValue = false;
            return ClipboardSnapshot.Placeholder with { PrimaryText = contents.PrimaryText };
        }
        var written = contents with
        {
            Text = Kept(ClipboardFormat.Text, contents.Text),
            ImagePng = Kept(ClipboardFormat.Image, contents.ImagePng),
            Html = Kept(ClipboardFormat.Html, contents.Html),
            Rtf = Kept(ClipboardFormat.Rtf, contents.Rtf),
        };
        Text = written.Text;
        ImagePng = written.ImagePng;
        Html = written.Html;
        Rtf = written.Rtf;
        // the platforms write the markers beside a concealed copy
        ConcealedValue = written.Concealed;
        _ownWrite = true;
        return written;
    }

    private T? Kept<T>(ClipboardFormat format, T? value) where T : class => RefusedFormats.Contains(format) ? null : value;

    // a write then keeps no clipboard format, though it was made
    public void RefuseEveryFormat() => RefusedFormats.UnionWith(Enum.GetValues<ClipboardFormat>());

    // the selection is left as it is; Hydra reads the clear's placeholder as empty, so the fake keeps nothing
    public bool ClearClipboard()
    {
        ClearClipboardCallCount++;
        if (ClearFailsValue) return false;
        _wroteAt = ++_changes;
        Text = null;
        ImagePng = null;
        Html = null;
        Rtf = null;
        ConcealedValue = false;
        return true;
    }

    // helpers for test setup (bypass the call counters)
    public void SetupText(string text)
    {
        Text = text;
        _ownWrite = false;
        _changes++;
    }

    public void SetupImage(byte[]? png)
    {
        ImagePng = png;
        _ownWrite = false;
        _changes++;
    }
}
