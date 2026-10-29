namespace Hydra.Platform;

// snapshot of clipboard state — the unit synced between peers and the echo-suppression fallback.
// Html is portable raw HTML (no Windows CF_HTML wrapper); Rtf is raw RTF bytes. Both are optional
// rich representations that ride alongside the plain Text (which stays the universal fallback).
// Concealed: a password manager's copy, kept out of history and cloud sync wherever it is written.
public record ClipboardSnapshot(string? Text, string? PrimaryText, byte[]? ImagePng, string? Html = null, byte[]? Rtf = null, bool Concealed = false)
{
    // anything for the clipboard itself, beside the selection
    public bool HasClipboard => !string.IsNullOrEmpty(Text) || ImagePng != null || Html != null || Rtf != null;

    public bool HasSelection => !string.IsNullOrEmpty(PrimaryText);

    public bool SelectionIsText => IsCopyOfText(PrimaryText, Text);

    public static bool IsCopyOfText(string? primaryText, string? text) => text != null && primaryText == text;

    public bool IsEmpty => !HasClipboard && !HasSelection;

    public bool IsSelectionOnly => !HasClipboard && HasSelection;

    public static ClipboardSnapshot? SelectionOnly(string? selection) => string.IsNullOrEmpty(selection) ? null : new ClipboardSnapshot(null, selection, null);

    // nothing for the clipboard, and no selection to change
    public static readonly ClipboardSnapshot Empty = new(null, null, null);

    // what a clear writes: clipboard managers refill an empty clipboard, which would read as the user's copy
    public const string PlaceholderText = "\u00A0";

    public static readonly ClipboardSnapshot Placeholder = new(PlaceholderText, null, null);

    // the placeholder alone, which is never content
    public static bool IsPlaceholder(string? text, byte[]? image, string? html, byte[]? rtf) =>
        text == PlaceholderText && image == null && html == null && rtf == null;

    // a clear's write: the placeholder, whatever selection beside it
    public bool IsClear => IsPlaceholder(Text, ImagePng, Html, Rtf);
}

// a clipboard that could not be read, which says nothing of what it holds: held open by another app, an owner that
// never answered, a native call that failed
public sealed class ClipboardUnreadableException(string message, Exception? inner = null) : Exception(message, inner);

// a format of the clipboard a write may refuse
public enum ClipboardFormat { Text, Html, Rtf, Image }

// one read of the clipboard's own formats, before any echo fallback; copied files are read as nothing else
public sealed record ClipboardRead(string? Text, byte[]? ImagePng, string? Html, byte[]? Rtf, bool Concealed, bool HasFiles = false)
{
    public static readonly ClipboardRead Empty = new(null, null, null, null, false);
    public static readonly ClipboardRead Files = new(null, null, null, null, false, HasFiles: true);
}

// one format a platform writes, and the write, which says whether the clipboard took it
public sealed record FormatWrite(ClipboardFormat Format, Func<bool> Write);

// ReadAll throws ClipboardUnreadableException when the clipboard cannot be read, never answering null or false for a
// clipboard it could not see; the selection reads as none when it cannot be read
public interface IClipboardSync
{
    // every format (HTML portable, without Windows' CF_HTML wrapper) and whether it carries a password manager's
    // concealed/transient markers, in one read
    ClipboardRead ReadAll();

    string? GetPrimaryText() => null;
    bool SetPrimaryText(string text) => false; // whether it kept it
    bool HasFileClipboard() => false;

    // True only while an all-null read can mean that this process still owns the clipboard and
    // ReadAll echo-suppressed its representations. Platforms that can track ownership should
    // return false after another process (or the system clipboard) takes ownership.
    bool CanUseEchoFallback() => true;

    // the OS clipboard's change counter, which only grows: moved by every copy (X11: only with XFixes, else only
    // on an owner change) and every one of Hydra's own writes, so a copy made since a point is told apart from content
    // that merely matches; null where the platform keeps none
    long? ChangeCount() => null;

    // the count Hydra's own last successful write or clear left on that counter, as the write itself saw it, so a copy
    // landing during or after it is never taken for that write; null where the platform does not report it
    long? WroteAt() => null;

    // atomically clears and writes the given contents, never a selection alone, with the selection beside them, and
    // returns what it wrote: the contents less any format the clipboard refused, null when it did not replace the
    // clipboard; empty contents leave it empty. A concealed copy's markers are written before its content, so no
    // clipboard manager or monitor sees the content without them; a copy whose markers were refused is never written
    // unprotected, the placeholder going in its place, as the snapshot returned says
    ClipboardSnapshot? SetClipboard(ClipboardSnapshot contents);

    // empties the clipboard as Hydra's own write, leaving the selection: the placeholder, which Hydra reads as empty and
    // never as concealed, marked no-history/no-cloud where the platform has such markers (kept out of clipboard history
    // and cloud sync, yet left for clipboard managers to see); whether it did
    bool ClearClipboard() => SetClipboard(ClipboardSnapshot.Placeholder) != null;
}
