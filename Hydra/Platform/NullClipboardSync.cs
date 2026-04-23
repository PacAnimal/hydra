namespace Hydra.Platform;

public class NullClipboardSync : IClipboardSync
{
    public string? GetText() => null;
    public void SetText(string text) { }
    public void SetClipboard(string? text, string? primaryText, byte[]? imagePng) { }
}
