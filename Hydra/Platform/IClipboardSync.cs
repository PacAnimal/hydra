namespace Hydra.Platform;

public interface IClipboardSync
{
    string? GetText();
    void SetText(string text);
    string? GetPrimaryText() => null;
    void SetPrimaryText(string text) { }
    byte[]? GetImagePng() => null;
    void SetImagePng(byte[] pngData) { }

    // atomically sets text and/or image in a single clipboard open/clear; default falls back to individual setters
    void SetClipboard(string? text, string? primaryText, byte[]? imagePng)
    {
        if (text == null && primaryText == null && imagePng == null) return;
        if (text != null) SetText(text);
        if (primaryText != null) SetPrimaryText(primaryText);
        if (imagePng != null) SetImagePng(imagePng);
    }
}
