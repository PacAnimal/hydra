using System.IO.Hashing;
using System.Text;
using ByteSizeLib;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform;

public static class ClipboardUtils
{
    public static readonly long MaxClipboardBytes = (long)ByteSize.FromMebiBytes(16).Bytes;

    // null-out any field that individually exceeds the limit
    public static ClipboardSnapshot ValidateFields(string? text, string? primaryText, byte[]? image, ILogger log, string context, string host)
    {
        var validText = !string.IsNullOrEmpty(text) && Encoding.UTF8.GetByteCount(text) <= MaxClipboardBytes ? text : null;
        var validPrimary = !string.IsNullOrEmpty(primaryText) && Encoding.UTF8.GetByteCount(primaryText) <= MaxClipboardBytes ? primaryText : null;
        var validImage = image?.Length <= MaxClipboardBytes ? image : null;
        if (validText == null && !string.IsNullOrEmpty(text))
            log.LogWarning("Clipboard {Context} from {Host}: text exceeds {Max} bytes, dropping", context, host, MaxClipboardBytes);
        if (validPrimary == null && !string.IsNullOrEmpty(primaryText))
            log.LogWarning("Clipboard {Context} from {Host}: primary text exceeds {Max} bytes, dropping", context, host, MaxClipboardBytes);
        if (validImage == null && image != null)
            log.LogWarning("Clipboard {Context} from {Host}: image exceeds {Max} bytes, dropping", context, host, MaxClipboardBytes);
        return new ClipboardSnapshot(validText, validPrimary, validImage);
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
        if (text == null && primaryText == null && image == null)
        {
            text = fallback?.Text;
            primaryText = fallback?.PrimaryText;
            image = fallback?.ImagePng;
        }
        return image != null
            ? TrimToFit(null, null, image, log, context)
            : TrimToFit(text, primaryText, null, log, context);
    }

    // drop fields in priority order (image, primary, text) until combined size fits
    public static ClipboardSnapshot TrimToFit(string? text, string? primaryText, byte[]? image, ILogger log, string context)
    {
        long textBytes = text != null ? Encoding.UTF8.GetByteCount(text) : 0;
        long primaryBytes = primaryText != null ? Encoding.UTF8.GetByteCount(primaryText) : 0;
        long imageBytes = image?.Length ?? 0;
        if (textBytes + primaryBytes + imageBytes > MaxClipboardBytes)
        {
            log.LogWarning("Clipboard {Context} too large ({Total} bytes), dropping image", context, textBytes + primaryBytes + imageBytes);
            image = null; imageBytes = 0;
        }
        if (textBytes + primaryBytes + imageBytes > MaxClipboardBytes)
        {
            log.LogWarning("Clipboard {Context} still too large ({Total} bytes), dropping primary text", context, textBytes + primaryBytes);
            primaryText = null; primaryBytes = 0;
        }
        if (textBytes + primaryBytes + imageBytes > MaxClipboardBytes)
        {
            log.LogWarning("Clipboard {Context} still too large ({Total} bytes), dropping text", context, textBytes);
            text = null;
        }
        return new ClipboardSnapshot(text, primaryText, image);
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
        return BitConverter.ToUInt64(hash.GetCurrentHash().AsSpan());

        static void Append(XxHash64 h, byte[] data)
        {
            h.Append(BitConverter.GetBytes(data.Length));
            h.Append(data);
        }
    }
}
