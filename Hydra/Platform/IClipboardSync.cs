namespace Hydra.Platform;

public interface IClipboardSync
{
    string? GetText();
    void SetText(string text);
}
