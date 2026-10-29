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
    // selection is read as it stands, never from the fallback. Null when the clipboard could not be read, which is
    // unknown, never empty
    public static ClipboardSnapshot? ReadWithFallback(IClipboardSync sync, ClipboardSnapshot? fallback, ILogger log, string context)
    {
        try
        {
            return Read(sync, fallback, log, context);
        }
        catch (ClipboardUnreadableException ex)
        {
            log.LogWarning(ex, "Clipboard {Context}: the clipboard could not be read, so nothing is known of it this time", context);
            return null;
        }
    }

    private static ClipboardSnapshot Read(IClipboardSync sync, ClipboardSnapshot? fallback, ILogger log, string context)
    {
        var read = sync.ReadAll();
        if (read.HasFiles)
        {
            if (log.IsEnabled(LogLevel.Debug))
                log.LogDebug("Clipboard {Context} contains files; preserving the native file clipboard", context);
            return TrimToFit(null, sync.GetPrimaryText(), null, null, null, log, context);
        }

        var text = read.Text;
        var primaryText = sync.GetPrimaryText();
        var image = read.ImagePng;
        var html = read.Html;
        var rtf = read.Rtf;
        var concealed = read.Concealed;
        // a clear's placeholder read back, as when Hydra no longer owns it, is no content, and never concealed
        if (ClipboardSnapshot.IsPlaceholder(text, image, html, rtf))
        {
            text = null;
            concealed = false;
        }
        if (text == null && image == null && html == null && rtf == null && sync.CanUseEchoFallback())
        {
            text = fallback?.Text;
            image = fallback?.ImagePng;
            html = fallback?.Html;
            rtf = fallback?.Rtf;
            concealed = fallback?.Concealed ?? false;
        }
        // image and rich text are mutually-exclusive copy actions: an image copy wins outright; otherwise
        // carry the text plus its rich (html/rtf) representations.
        var trimmed = image != null
            ? TrimToFit(null, primaryText, image, null, null, log, context)
            : TrimToFit(text, primaryText, null, html, rtf, log, context);
        return concealed ? trimmed with { Concealed = true } : trimmed;
    }

    // a hash every host computes alike, for what crosses the wire
    public static ulong StableHash(string text) => XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(text));

    // a write's contents once its markers are written, before any format, see IClipboardSync.SetClipboard: a concealed
    // copy's markers, the placeholder going in its place when they are refused, and the placeholder's no-history ones
    public static ClipboardSnapshot WriteMarkers(ILogger log, ClipboardSnapshot contents, Func<bool> writeConcealedMarkers,
        Func<bool> writePlaceholderMarkers)
    {
        var marked = contents;
        if (contents.Concealed && !writeConcealedMarkers())
        {
            log.LogWarning("The clipboard refused the concealed markers; the placeholder was written in place of the copy");
            marked = ClipboardSnapshot.Placeholder with { PrimaryText = contents.PrimaryText };
        }
        if (marked.IsClear && !writePlaceholderMarkers())
            log.LogWarning("The clipboard refused the no-history markers on its placeholder");
        return marked;
    }

    // every format of a write of contents, in the platform's order, each null when absent; one the clipboard refuses is
    // warned about, and the rest are still written. What was written: contents less the refused formats
    public static ClipboardSnapshot WriteAll(ILogger log, ClipboardSnapshot contents, params FormatWrite?[] writes)
    {
        var refused = writes.Where(w => w != null && !w.Write()).Select(w => w!.Format).ToList();
        if (refused.Count == 0) return contents;
        log.LogWarning("The clipboard refused {Refused}; the rest were written", string.Join(", ", refused));
        return refused.Aggregate(contents, Without);
    }

    private static ClipboardSnapshot Without(ClipboardSnapshot s, ClipboardFormat format) => format switch
    {
        ClipboardFormat.Text => s with { Text = null },
        ClipboardFormat.Html => s with { Html = null },
        ClipboardFormat.Rtf => s with { Rtf = null },
        _ => s with { ImagePng = null },
    };

    // applies what it may and returns that, null for nothing: a selection alone, or beside the user's copied files,
    // leaves the clipboard as it is, and a format the clipboard refused is not in it; written is told what the
    // clipboard's write returned, whenever there was one
    public static ClipboardSnapshot? ApplyPreservingFiles(IClipboardSync sync, ClipboardSnapshot contents, ILogger log, string context,
        Action<ClipboardSnapshot?>? written = null)
    {
        if (contents.IsEmpty) return null;
        if (contents.IsSelectionOnly) return KeepSelection(sync, contents.PrimaryText);
        if (sync.HasFileClipboard())
        {
            if (log.IsEnabled(LogLevel.Information))
                log.LogInformation("Clipboard {Context} skipped because the local clipboard contains files", context);
            return KeepSelection(sync, contents.PrimaryText);
        }

        // set again after a failed write, or one that took no format, since only SetPrimaryText says whether this host
        // could keep it; the placeholder written in place of a copy whose markers were refused is no copy applied
        var wrote = sync.SetClipboard(contents);
        written?.Invoke(wrote);
        return wrote is { HasClipboard: true } && (!wrote.IsClear || contents.IsClear) ? wrote : KeepSelection(sync, contents.PrimaryText);
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
