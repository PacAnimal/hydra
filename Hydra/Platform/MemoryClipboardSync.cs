namespace Hydra.Platform;

// a headless master's clipboard: the last copy a slave sent, for the next one. Only Hydra writes it, so the counter
// moves only with those writes
public sealed class MemoryClipboardSync : IClipboardSync
{
    private readonly Lock _lock = new();
    private ClipboardSnapshot _contents = ClipboardSnapshot.Empty;
    private string? _primary;
    private long _changes;

    public ClipboardRead ReadAll()
    {
        lock (_lock)
            return new ClipboardRead(_contents.Text, _contents.ImagePng, _contents.Html, _contents.Rtf, _contents.Concealed);
    }

    public string? GetPrimaryText()
    {
        lock (_lock)
            return _primary;
    }

    public bool SetPrimaryText(string text)
    {
        lock (_lock)
            _primary = text;
        return true;
    }

    public long? ChangeCount()
    {
        lock (_lock)
            return _changes;
    }

    public ClipboardSnapshot SetClipboard(ClipboardSnapshot contents)
    {
        lock (_lock)
        {
            // the selection is kept apart, and stays through a write without one, as a clear
            if (contents.PrimaryText != null) _primary = contents.PrimaryText;
            _contents = contents with { PrimaryText = null };
            _changes++;
        }
        return contents;
    }
}
