namespace Hydra.Platform;

// snapshot of clipboard state used as echo suppression fallback when Get* returns null
public record ClipboardSnapshot(string? Text, string? PrimaryText, byte[]? ImagePng, byte[]? Zip);

public interface IClipboardSync
{
    string? GetText();
    void SetText(string text);
    string? GetPrimaryText() => null;
    void SetPrimaryText(string text) { }
    byte[]? GetImagePng() => null;
    void SetImagePng(byte[] pngData) { }

    // atomically clears and writes text, image, and/or files in a single clipboard open.
    // WARNING: the default implementation calls individual setters sequentially — each one clears the clipboard,
    // so only the last format survives. any real platform implementation must override this method.
    void SetClipboard(string? text, string? primaryText, byte[]? imagePng, List<TempFileEntry>? files = null)
    {
        if (text == null && primaryText == null && imagePng == null && files == null) return;
        if (text != null) SetText(text);
        if (primaryText != null) SetPrimaryText(primaryText);
        if (imagePng != null) SetImagePng(imagePng);
    }

    bool SupportsFiles => false;
    List<string>? GetFilePaths() => null;
}
