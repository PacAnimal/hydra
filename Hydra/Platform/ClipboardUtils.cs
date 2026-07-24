using System.IO.Hashing;
using System.Text;
using ByteSizeLib;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform;

internal struct ClipboardEchoFilter
{
    private string? _lastText;
    private ulong? _lastImageHash;
    private ulong? _lastHtmlHash;
    private ulong? _lastRtfHash;

    public void TrackText(string text) => _lastText = text;
    public void TrackImage(byte[] png) => _lastImageHash = ClipboardUtils.QuickHash(png);
    public void TrackHtml(string html) => _lastHtmlHash = ClipboardUtils.QuickHash(Encoding.UTF8.GetBytes(html));
    public void TrackRtf(byte[] rtf) => _lastRtfHash = ClipboardUtils.QuickHash(rtf);
    public readonly string? FilterText(string? text) => text == _lastText ? null : text;
    public readonly bool IsDuplicateImage(byte[] png) => _lastImageHash.HasValue && ClipboardUtils.QuickHash(png) == _lastImageHash.Value;
    public readonly string? FilterHtml(string? html) => html != null && _lastHtmlHash.HasValue && ClipboardUtils.QuickHash(Encoding.UTF8.GetBytes(html)) == _lastHtmlHash.Value ? null : html;
    public readonly byte[]? FilterRtf(byte[]? rtf) => rtf != null && _lastRtfHash.HasValue && ClipboardUtils.QuickHash(rtf) == _lastRtfHash.Value ? null : rtf;
}

public static class ClipboardUtils
{
    public static readonly long MaxClipboardBytes = (long)ByteSize.FromMebiBytes(16).Bytes;

    // null-out any field that individually exceeds the limit
    public static ClipboardSnapshot ValidateFields(string? text, string? primaryText, byte[]? image, string? html, byte[]? rtf, ILogger log, string context, string host)
    {
        var validText = !string.IsNullOrEmpty(text) && Encoding.UTF8.GetByteCount(text) <= MaxClipboardBytes ? text : null;
        var validPrimary = !string.IsNullOrEmpty(primaryText) && Encoding.UTF8.GetByteCount(primaryText) <= MaxClipboardBytes ? primaryText : null;
        var validImage = image?.Length <= MaxClipboardBytes ? image : null;
        var validHtml = !string.IsNullOrEmpty(html) && Encoding.UTF8.GetByteCount(html) <= MaxClipboardBytes ? html : null;
        var validRtf = rtf?.Length <= MaxClipboardBytes ? rtf : null;
        if (validText == null && !string.IsNullOrEmpty(text))
            log.LogWarning("Clipboard {Context} from {Host}: text exceeds {Max} bytes, dropping", context, host, MaxClipboardBytes);
        if (validPrimary == null && !string.IsNullOrEmpty(primaryText))
            log.LogWarning("Clipboard {Context} from {Host}: primary text exceeds {Max} bytes, dropping", context, host, MaxClipboardBytes);
        if (validImage == null && image != null)
            log.LogWarning("Clipboard {Context} from {Host}: image exceeds {Max} bytes, dropping", context, host, MaxClipboardBytes);
        if (validHtml == null && !string.IsNullOrEmpty(html))
            log.LogWarning("Clipboard {Context} from {Host}: html exceeds {Max} bytes, dropping", context, host, MaxClipboardBytes);
        if (validRtf == null && rtf != null)
            log.LogWarning("Clipboard {Context} from {Host}: rtf exceeds {Max} bytes, dropping", context, host, MaxClipboardBytes);
        return new ClipboardSnapshot(validText, validPrimary, validImage, validHtml, validRtf);
    }

    // reads from sync, falling back to snapshot fields when Get* returns null (echo suppression).
    //
    // Get*() returns null for two distinct reasons that we cannot tell apart:
    //   (a) the type is genuinely absent from the pasteboard
    //   (b) the type is present but Hydra wrote it, so it is echo-suppressed
    //
    // the fallback exists solely to handle (b). we only apply it when ALL fields are null,
    // meaning everything is echo-suppressed and the user has not copied anything new.
    // if ANY field is non-null (fresh user copy), we skip the fallback entirely — mixing a
    // freshly-copied type with a stale fallback field would resurrect data from an older operation.
    //
    // "which type did the user copy last?" is implicitly encoded in what is ABSENT from the
    // pasteboard: every copy operation calls clearContents first, so text and image can only
    // coexist when they came from the exact same copy action. if the user copied text after image,
    // the image slot is empty and GetImagePng() returns null — no fallback image can sneak in
    // because text being non-null keeps us out of the fallback block. same logic in reverse.
    //
    // when both text and image are genuinely present (written together by one copy action, e.g.
    // Finder copying an image file), image wins — the text is just a fallback representation the
    // source app added, not something the user explicitly copied as text.
    public static ClipboardSnapshot ReadWithFallback(IClipboardSync sync, ClipboardSnapshot? fallback, ILogger log, string context)
    {
        var text = sync.GetText();
        var primaryText = sync.GetPrimaryText();
        var image = sync.GetImagePng();
        var html = sync.GetHtml();
        var rtf = sync.GetRtf();
        if (text == null && primaryText == null && image == null && html == null && rtf == null)
        {
            text = fallback?.Text;
            primaryText = fallback?.PrimaryText;
            image = fallback?.ImagePng;
            html = fallback?.Html;
            rtf = fallback?.Rtf;
        }
        // image and rich text are mutually-exclusive copy actions: an image copy wins outright; otherwise
        // carry the text plus its rich (html/rtf) representations.
        return image != null
            ? TrimToFit(null, null, image, null, null, log, context)
            : TrimToFit(text, primaryText, null, html, rtf, log, context);
    }

    // drop fields until the combined size fits. order keeps the universal plain text LAST (it's the
    // Notepad-equivalent fallback): image, then the rich reps (html, rtf), then primary text, then text.
    public static ClipboardSnapshot TrimToFit(string? text, string? primaryText, byte[]? image, string? html, byte[]? rtf, ILogger log, string context)
    {
        long textBytes = text != null ? Encoding.UTF8.GetByteCount(text) : 0;
        long primaryBytes = primaryText != null ? Encoding.UTF8.GetByteCount(primaryText) : 0;
        long imageBytes = image?.Length ?? 0;
        long htmlBytes = html != null ? Encoding.UTF8.GetByteCount(html) : 0;
        long rtfBytes = rtf?.Length ?? 0;
        long Total() => textBytes + primaryBytes + imageBytes + htmlBytes + rtfBytes;

        if (Total() > MaxClipboardBytes)
        {
            log.LogWarning("Clipboard {Context} too large ({Total} bytes), dropping image", context, Total());
            image = null; imageBytes = 0;
        }
        if (Total() > MaxClipboardBytes)
        {
            log.LogWarning("Clipboard {Context} still too large ({Total} bytes), dropping html", context, Total());
            html = null; htmlBytes = 0;
        }
        if (Total() > MaxClipboardBytes)
        {
            log.LogWarning("Clipboard {Context} still too large ({Total} bytes), dropping rtf", context, Total());
            rtf = null; rtfBytes = 0;
        }
        if (Total() > MaxClipboardBytes)
        {
            log.LogWarning("Clipboard {Context} still too large ({Total} bytes), dropping primary text", context, Total());
            primaryText = null; primaryBytes = 0;
        }
        if (Total() > MaxClipboardBytes)
        {
            log.LogWarning("Clipboard {Context} still too large ({Total} bytes), dropping text", context, Total());
            text = null;
        }
        return new ClipboardSnapshot(text, primaryText, image, html, rtf);
    }

    public static ulong QuickHash(byte[] data)
    {
        // two hashes with different inputs combined into 64-bit to reduce collision probability
        var hc1 = new HashCode();
        hc1.AddBytes(data);
        var hc2 = new HashCode();
        hc2.Add(data.Length); // prefix with length to differentiate from hc1
        hc2.AddBytes(data);
        return ((ulong)(uint)hc1.ToHashCode() << 32) | (uint)hc2.ToHashCode();
    }

    // xxhash64 of all 3 clipboard fields; used to avoid redundant syncs between master and slave
    public static ulong ClipboardHash(ClipboardSnapshot snap)
    {
        var hash = new XxHash64();
        Append(hash, snap.Text != null ? Encoding.UTF8.GetBytes(snap.Text) : []);
        Append(hash, snap.PrimaryText != null ? Encoding.UTF8.GetBytes(snap.PrimaryText) : []);
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
