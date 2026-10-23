using Microsoft.Extensions.Logging;

namespace Hydra.Relay;

// encodes a clipboard message within the receiver's cap (ClipboardStreamReceiver.MaxTotalBytes): TrimToFit counts raw
// bytes, but json escaping and base64 can grow them past it, so fields are dropped in TrimToFit's order until it fits
public static class ClipboardEncoding
{
    public static EncodedClipboard<T> EncodeWithinCap<T>(MessageKind kind, T message, ILogger log) where T : IClipboardPayload
    {
        var cap = ClipboardStreamReceiver.MaxTotalBytes;
        var blob = MessageSerializer.Encode(kind, message);
        if (blob.Length <= cap) return new EncodedClipboard<T>(message, blob);
        // the selection goes first if that alone makes it fit, as the one thing the user did not copy
        if (Without(message, Field.PrimaryText) is { } unselected && MessageSerializer.Encode(kind, unselected) is { } shorter && shorter.Length <= cap)
        {
            log.LogWarning("Clipboard {Kind} encodes to {Bytes} bytes, over {Max}, dropping {Field}", kind, blob.Length, cap, Name(Field.PrimaryText));
            return new EncodedClipboard<T>(unselected, shorter);
        }
        foreach (var field in Enum.GetValues<Field>())
        {
            if (blob.Length <= cap) break;
            if (Without(message, field) is not { } trimmed) continue;
            log.LogWarning("Clipboard {Kind} encodes to {Bytes} bytes, over {Max}, dropping {Field}", kind, blob.Length, cap, Name(field));
            message = trimmed;
            blob = MessageSerializer.Encode(kind, message);
        }
        return new EncodedClipboard<T>(message, blob);
    }

    // in the order they are dropped, TrimToFit's
    private enum Field { Image, Html, Rtf, PrimaryText, Text }

    private static string Name(Field field) => field == Field.PrimaryText ? "primary text" : field.ToString().ToLowerInvariant();

    // the message without field, or null when it has none; a push's empty text is none, and a primary still left
    // repeats the text, so it goes with it
    private static T? Without<T>(T message, Field field) where T : IClipboardPayload
    {
        var has = field switch
        {
            Field.Image => message.ImagePng != null,
            Field.Html => message.Html != null,
            Field.Rtf => message.Rtf != null,
            Field.PrimaryText => message.PrimaryText != null,
            _ => message is ClipboardPushMessage ? !string.IsNullOrEmpty(message.Text) : message.Text != null,
        };
        if (!has) return default;
        var image = field == Field.Image ? null : message.ImagePng;
        var html = field == Field.Html ? null : message.Html;
        var rtf = field == Field.Rtf ? null : message.Rtf;
        var primary = field == Field.PrimaryText ? null : message.PrimaryText;
        var primaryIsText = field == Field.Text ? null : message.PrimaryIsText;
        var text = field == Field.Text ? null : message.Text;
        object trimmed = message switch
        {
            ClipboardPushMessage m => m with
            {
                Text = text ?? "",
                PrimaryText = primary,
                ImagePng = image,
                Html = html,
                Rtf = rtf,
                PrimaryIsText = primaryIsText,
            },
            ClipboardPullResponseMessage m => m with
            {
                Text = text,
                PrimaryText = primary,
                ImagePng = image,
                Html = html,
                Rtf = rtf,
                PrimaryIsText = primaryIsText,
            },
            _ => throw new ArgumentException($"not a clipboard payload: {message.GetType().Name}", nameof(message)),
        };
        return (T)trimmed;
    }
}

public sealed record EncodedClipboard<T>(T Message, byte[] Blob) where T : IClipboardPayload;
