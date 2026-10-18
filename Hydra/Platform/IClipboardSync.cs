namespace Hydra.Platform;

// snapshot of clipboard state — the unit synced between peers and the echo-suppression fallback.
// Html is portable raw HTML (no Windows CF_HTML wrapper); Rtf is raw RTF bytes. Both are optional
// rich representations that ride alongside the plain Text (which stays the universal fallback).
public record ClipboardSnapshot(string? Text, string? PrimaryText, byte[]? ImagePng, string? Html = null, byte[]? Rtf = null)
{
    // anything for the clipboard itself, beside the selection
    public bool HasClipboard => !string.IsNullOrEmpty(Text) || ImagePng != null || Html != null || Rtf != null;

    public bool HasSelection => !string.IsNullOrEmpty(PrimaryText);

    public bool SelectionIsText => IsCopyOfText(PrimaryText, Text);

    public static bool IsCopyOfText(string? primaryText, string? text) => text != null && primaryText == text;

    public bool IsEmpty => !HasClipboard && !HasSelection;

    public bool IsSelectionOnly => !HasClipboard && HasSelection;

    public static ClipboardSnapshot? SelectionOnly(string? selection) => string.IsNullOrEmpty(selection) ? null : new ClipboardSnapshot(null, selection, null);
}

public interface IClipboardSync
{
    string? GetText();
    string? GetPrimaryText() => null;
    bool SetPrimaryText(string text) => false; // whether it kept it
    byte[]? GetImagePng() => null;
    string? GetHtml() => null;   // portable raw HTML (platforms that don't support it return null)
    byte[]? GetRtf() => null;
    bool HasFileClipboard() => false;

    // True only while an all-null read can mean that this process still owns the clipboard and
    // the getters echo-suppressed its representations. Platforms that can track ownership should
    // return false after another process (or the system clipboard) takes ownership.
    bool CanUseEchoFallback() => true;

    // atomically clears and writes the given contents, never a selection alone, with the selection beside them, and says
    // whether it replaced the clipboard. every platform implementation must override this — there is no safe default.
    bool SetClipboard(ClipboardSnapshot contents) =>
        throw new NotImplementedException($"{GetType().Name} must override SetClipboard");
}
