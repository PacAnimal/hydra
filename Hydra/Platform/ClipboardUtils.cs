using System.IO.Hashing;
using System.Text;
using ByteSizeLib;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform;

// what reads back as Hydra's own write, while owned says the clipboard is still that write
internal sealed class ClipboardEchoFilter(Func<bool> owned)
{
    private string? _lastText;
    private ulong? _lastImageHash;
    private ulong? _lastHtmlHash;
    private ulong? _lastRtfHash;

    // what Hydra has just written, so reading it back is an echo; every format, since the write replaced them all
    public void Track(ClipboardSnapshot written)
    {
        _lastText = written.Text;
        _lastImageHash = Hash(written.ImagePng);
        _lastHtmlHash = Hash(written.Html);
        _lastRtfHash = Hash(written.Rtf);
    }
    public string? FilterText(string? text) => text != null && text == _lastText && owned() ? null : text;
    public bool IsDuplicateImage(byte[] png) => Echoes(_lastImageHash, Hash(png));
    public string? FilterHtml(string? html) => Echoes(_lastHtmlHash, Hash(html)) ? null : html;
    public byte[]? FilterRtf(byte[]? rtf) => Echoes(_lastRtfHash, Hash(rtf)) ? null : rtf;

    private bool Echoes(ulong? written, ulong? read) => written != null && read == written && owned();
    private static ulong? Hash(byte[]? data) => data == null ? null : XxHash64.HashToUInt64(data);
    private static ulong? Hash(string? text) => text == null ? null : Hash(Encoding.UTF8.GetBytes(text));
}

public static class ClipboardUtils
{
    public static readonly long MaxClipboardBytes = (long)ByteSize.FromMebiBytes(16).Bytes;

    // null-out any field that individually exceeds the limit
    public static ClipboardSnapshot ValidateFields(string? text, string? primaryText, byte[]? image, string? html, byte[]? rtf, ILogger log, string context, string host) =>
        new(Fit(text, "text", log, context, host), Fit(primaryText, "primary text", log, context, host), Fit(image, "image", log, context, host),
            Fit(html, "html", log, context, host), Fit(rtf, "rtf", log, context, host));

    // the text if it is within the limit, else null and a warning; empty is null
    internal static string? Fit(string? text, string what, ILogger log, string context, string host) =>
        string.IsNullOrEmpty(text) ? null : Fits(Encoding.UTF8.GetByteCount(text), what, log, context, host) ? text : null;

    private static byte[]? Fit(byte[]? data, string what, ILogger log, string context, string host) =>
        data != null && Fits(data.Length, what, log, context, host) ? data : null;

    private static bool Fits(long bytes, string what, ILogger log, string context, string host)
    {
        if (bytes <= MaxClipboardBytes) return true;
        log.LogWarning("Clipboard {Context} from {Host}: {What} exceeds {Max} bytes, dropping", context, host, what, MaxClipboardBytes);
        return false;
    }

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
        var selection = ClipboardSnapshot.SelectionOnly(contents.PrimaryText);
        if (contents.IsSelectionOnly) return KeepSelection(sync, selection);
        if (sync.HasFileClipboard())
        {
            if (log.IsEnabled(LogLevel.Information))
                log.LogInformation("Clipboard {Context} skipped because the local clipboard contains files", context);
            return KeepSelection(sync, selection);
        }

        // set again after a failed write, since only SetPrimaryText says whether this host could keep it
        return sync.SetClipboard(contents) ? contents : KeepSelection(sync, selection);
    }

    private static ClipboardSnapshot? KeepSelection(IClipboardSync sync, ClipboardSnapshot? selection) =>
        selection?.PrimaryText is { } primary && sync.SetPrimaryText(primary) ? selection : null;

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
        void Dropping(string what) => log.LogWarning("Clipboard {Context} too large ({Total} bytes), dropping {What}", context, Total(), what);

        if (Over() && primaryBytes > 0 && Total() - primaryBytes <= MaxClipboardBytes)
        {
            Dropping("primary text");
            primaryText = null; primaryBytes = 0;
        }
        if (Over())
        {
            Dropping("image");
            image = null; imageBytes = 0;
        }
        if (Over())
        {
            Dropping("html");
            html = null; htmlBytes = 0;
        }
        if (Over())
        {
            Dropping("rtf");
            rtf = null; rtfBytes = 0;
        }
        if (Over() && primaryBytes > 0)
        {
            Dropping("primary text");
            primaryText = null; primaryBytes = 0;
        }
        if (Over())
        {
            Dropping("text");
            text = null;
            primaryText = null; // any primary left was a copy of the text, so it goes with it
        }
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
