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

    // reads from sync, falling back to snapshot fields when Get* returns null (echo suppression)
    public static ClipboardSnapshot ReadWithFallback(IClipboardSync sync, ClipboardSnapshot? fallback, ILogger log, string context)
        => TrimToFit(sync.GetText() ?? fallback?.Text, sync.GetPrimaryText() ?? fallback?.PrimaryText, sync.GetImagePng() ?? fallback?.ImagePng, log, context);

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
}
