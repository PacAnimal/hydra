namespace Hydra.Platform;

// snapshot of clipboard state — the unit synced between peers and the echo-suppression fallback.
// Html is portable raw HTML (no Windows CF_HTML wrapper); Rtf is raw RTF bytes. Both are optional
// rich representations that ride alongside the plain Text (which stays the universal fallback).
public record ClipboardSnapshot(string? Text, string? PrimaryText, byte[]? ImagePng, string? Html = null, byte[]? Rtf = null);

public interface IClipboardSync
{
    string? GetText();
    void SetText(string text);
    string? GetPrimaryText() => null;
    void SetPrimaryText(string text) { }
    byte[]? GetImagePng() => null;
    void SetImagePng(byte[] pngData) { }
    string? GetHtml() => null;   // portable raw HTML (platforms that don't support it return null)
    byte[]? GetRtf() => null;

    // atomically clears and writes the given contents in a single clipboard open.
    // every platform implementation must override this — there is no safe default.
    void SetClipboard(ClipboardSnapshot contents) =>
        throw new NotImplementedException($"{GetType().Name} must override SetClipboard");
}
