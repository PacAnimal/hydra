namespace Hydra.Platform;

// snapshot of clipboard state used as echo suppression fallback when Get* returns null
public record ClipboardSnapshot(string? Text, string? PrimaryText, byte[]? ImagePng);

public interface IClipboardSync
{
    string? GetText();
    void SetText(string text);
    string? GetPrimaryText() => null;
    void SetPrimaryText(string text) { }
    byte[]? GetImagePng() => null;
    void SetImagePng(byte[] pngData) { }

    // atomically clears and writes text and/or image in a single clipboard open.
    // every platform implementation must override this — there is no safe default.
    void SetClipboard(string? text, string? primaryText, byte[]? imagePng) =>
        throw new NotImplementedException($"{GetType().Name} must override SetClipboard");
}
