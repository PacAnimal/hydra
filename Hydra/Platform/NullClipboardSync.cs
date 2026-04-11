namespace Hydra.Platform;

public class NullClipboardSync : IClipboardSync
{
    public string? GetText() => null;
    public void SetText(string text) { }
}
