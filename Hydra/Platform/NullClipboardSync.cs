namespace Hydra.Platform;

public class NullClipboardSync : IClipboardSync
{
    public ClipboardRead ReadAll() => ClipboardRead.Empty;
    public ClipboardSnapshot? SetClipboard(ClipboardSnapshot contents) => null;
}
