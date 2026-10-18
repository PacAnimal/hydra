using System.IO.Hashing;
using System.Text;
using ByteSizeLib;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform;

public static class ClipboardUtils
{
    public static readonly long MaxClipboardBytes = (long)ByteSize.FromMebiBytes(16).Bytes;

    // reads from sync, falling back to the snapshot only when every clipboard field is null and the platform says that can
    // still mean its own write was echo-suppressed; a fresh copy of any one type keeps stale fields from coming back. The
    // selection is read as it stands, never from the fallback.
    public static ClipboardSnapshot ReadWithFallback(IClipboardSync sync, ClipboardSnapshot? fallback, ILogger log, string context)
    {
        if (sync.HasFileClipboard())
        {
            if (log.IsEnabled(LogLevel.Debug))
                log.LogDebug("Clipboard {Context} contains files; preserving the native file clipboard", context);
            return TrimToFit(null, sync.GetPrimaryText(), null, null, null, log, context);
        }

        var text = sync.GetText();
        var primaryText = sync.GetPrimaryText();
        var image = sync.GetImagePng();
        var html = sync.GetHtml();
        var rtf = sync.GetRtf();
        if (text == null && image == null && html == null && rtf == null && sync.CanUseEchoFallback())
        {
            text = fallback?.Text;
            image = fallback?.ImagePng;
            html = fallback?.Html;
            rtf = fallback?.Rtf;
        }
        // image and rich text are mutually-exclusive copy actions: an image copy wins outright; otherwise
        // carry the text plus its rich (html/rtf) representations.
        return image != null
            ? TrimToFit(null, primaryText, image, null, null, log, context)
            : TrimToFit(text, primaryText, null, html, rtf, log, context);
    }

    // a hash every host computes alike, for what crosses the wire
    public static ulong StableHash(string text) => XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(text));

    // every format of a write, in the platform's order, each null when absent; one the clipboard refuses is warned about,
    // and the rest are still written
    public static void WriteAll(ILogger log, params Func<bool>?[] writes)
    {
        var refused = writes.Count(write => write != null && !write());
        if (refused > 0) log.LogWarning("The clipboard refused {Refused} of its formats; the rest were written", refused);
    }

    // applies what it may and returns that, null for nothing: a selection alone, or beside the user's copied files,
    // leaves the clipboard as it is
    public static ClipboardSnapshot? ApplyPreservingFiles(IClipboardSync sync, ClipboardSnapshot contents, ILogger log, string context)
    {
        if (contents.IsEmpty) return null;
        if (contents.IsSelectionOnly) return KeepSelection(sync, contents.PrimaryText);
        if (sync.HasFileClipboard())
        {
            if (log.IsEnabled(LogLevel.Information))
                log.LogInformation("Clipboard {Context} skipped because the local clipboard contains files", context);
            return KeepSelection(sync, contents.PrimaryText);
        }

        // set again after a failed write, since only SetPrimaryText says whether this host could keep it
        return sync.SetClipboard(contents) ? contents : KeepSelection(sync, contents.PrimaryText);
    }

    private static ClipboardSnapshot? KeepSelection(IClipboardSync sync, string? selection) =>
        selection != null && sync.SetPrimaryText(selection) ? ClipboardSnapshot.SelectionOnly(selection) : null;

    // drop fields until the combined size fits. the selection goes first if that alone makes it fit, as the one thing
    // the user did not copy; then image, the rich reps (html, rtf), the selection, and LAST the universal plain text
    // (it's the Notepad-equivalent fallback).
    public static ClipboardSnapshot TrimToFit(string? text, string? primaryText, byte[]? image, string? html, byte[]? rtf, ILogger log, string context)
    {
        long textBytes = text != null ? Encoding.UTF8.GetByteCount(text) : 0;
        long primaryBytes = primaryText != null && !ClipboardSnapshot.IsCopyOfText(primaryText, text) ? Encoding.UTF8.GetByteCount(primaryText) : 0;
        long imageBytes = image?.Length ?? 0;
        long htmlBytes = html != null ? Encoding.UTF8.GetByteCount(html) : 0;
        long rtfBytes = rtf?.Length ?? 0;
        long Total() => textBytes + primaryBytes + imageBytes + htmlBytes + rtfBytes;
        bool Over() => Total() > MaxClipboardBytes;

        // drops a field still present while over the limit
        bool Drop(string what, ref long bytes)
        {
            if (bytes == 0 || !Over()) return false;
            log.LogWarning("Clipboard {Context} too large ({Total} bytes), dropping {What}", context, Total(), what);
            bytes = 0;
            return true;
        }

        if (Total() - primaryBytes <= MaxClipboardBytes && Drop("primary text", ref primaryBytes)) primaryText = null;
        if (Drop("image", ref imageBytes)) image = null;
        if (Drop("html", ref htmlBytes)) html = null;
        if (Drop("rtf", ref rtfBytes)) rtf = null;
        if (Drop("primary text", ref primaryBytes)) primaryText = null;
        // any primary left was a copy of the text, so it goes with it
        if (Drop("text", ref textBytes)) { text = null; primaryText = null; }
        return new ClipboardSnapshot(text, primaryText, image, html, rtf);
    }

    // xxhash64 of the clipboard's fields, to skip redundant syncs
    public static ulong ClipboardHash(ClipboardSnapshot snap)
    {
        var hash = new XxHash64();
        Append(hash, snap.Text != null ? Encoding.UTF8.GetBytes(snap.Text) : []);
        Append(hash, snap.ImagePng ?? []);
        Append(hash, snap.Html != null ? Encoding.UTF8.GetBytes(snap.Html) : []);
        Append(hash, snap.Rtf ?? []);
        return BitConverter.ToUInt64(hash.GetCurrentHash().AsSpan());

        static void Append(XxHash64 h, byte[] data)
        {
            h.Append(BitConverter.GetBytes(data.Length));
            h.Append(data);
        }
    }
}
