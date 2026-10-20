namespace Hydra.Platform;

public class NullClipboardSync : IClipboardSync
{
    public string? GetText() => null;
    public bool SetClipboard(ClipboardSnapshot contents) => false;
}
