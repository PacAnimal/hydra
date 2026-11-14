using System.Text;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Linux;

// The Wayland clipboard's MIME types and how a read of them becomes a ClipboardRead, shared by the portal's clipboard
// and its wl-clipboard fallback.
internal static class WaylandMime
{
    internal const string Png = "image/png";
    internal const string Html = "text/html";
    internal const string Rtf = "text/rtf";

    // KDE's mark of a password manager's copy (wl-copy --sensitive offers it), which reads "secret"
    internal const string PasswordHint = "x-kde-passwordManagerHint";
    internal static readonly byte[] Secret = "secret"u8.ToArray();
    // the most a hint is read for
    internal const int HintMaxBytes = 64;

    // the UTF-8 text types a write offers, the preferred first
    internal static readonly string[] OfferedText = ["text/plain;charset=utf-8", "UTF8_STRING", "text/plain"];

    // the text types a read takes, the preferred first: the MIME types a Wayland-native owner serves, then the X11 atom
    // names, which only an X client bridged through XWayland may serve alone
    internal static readonly string[] Text = ["text/plain;charset=utf-8", "text/plain", "UTF8_STRING", "TEXT", "STRING"];

    internal const int ChunkBytes = 64 * 1024;

    internal static bool IsSecret(byte[]? hint) => hint != null && Encoding.UTF8.GetString(hint).Trim() == "secret";

    // One read of the offered formats. read gives a type's bytes, at most the cap it is passed (int.MaxValue: the
    // clipboard's own), null when it is not there or runs past the cap, and
    // throws ClipboardUnreadableException when that type could not be read. The text is the first offered text type
    // that reads, by preference, a failed one falling through to the next; any other format that could not be read is
    // left out; a hint that could not be read conceals, so a password manager's copy is never taken for plain text.
    // Unreadable only when something failed and nothing could be read (unknown, never empty); nothing there is Empty
    internal static ClipboardRead ReadOffered(IReadOnlySet<string> offered, Func<string, int, byte[]?> read, ILogger log)
    {
        var failed = new List<string>();
        ClipboardUnreadableException? failure = null;

        byte[]? Try(string type)
        {
            try
            {
                return read(type, type == PasswordHint ? HintMaxBytes : int.MaxValue);
            }
            catch (ClipboardUnreadableException e)
            {
                if (log.IsEnabled(LogLevel.Debug)) log.LogDebug(e, "The clipboard's {MimeType} could not be read", type);
                failed.Add(type);
                failure ??= e;
                return null;
            }
        }

        byte[]? text = null;
        foreach (var type in Text)
            if (offered.Contains(type) && (text = NonEmpty(Try(type))) != null)
                break;
        var png = offered.Contains(Png) ? Try(Png) : null;
        var html = offered.Contains(Html) ? Try(Html) : null;
        var rtf = offered.Contains(Rtf) ? Try(Rtf) : null;
        var hint = offered.Contains(PasswordHint) ? Try(PasswordHint) : null;
        var concealed = failed.Contains(PasswordHint) || IsSecret(hint);

        var result = Read(text, png, html, rtf, concealed);
        if (failure == null) return result;
        if (ReferenceEquals(result, ClipboardRead.Empty))
            throw new ClipboardUnreadableException($"none of the clipboard's {string.Join(", ", failed)} could be read", failure);

        // a text type that fell through to another is no loss; a format left out is
        var lost = failed.Where(type => !Text.Contains(type) || text == null).ToList();
        if (lost.Count > 0 && log.IsEnabled(LogLevel.Warning))
            log.LogWarning("The clipboard's {MimeTypes} could not be read; read without them", string.Join(", ", lost));
        return result;
    }

    // A stream read to its end in chunks; null once it runs past maxBytes. beforeRead runs ahead of each read, to wait
    // for data and throw at a deadline.
    internal static byte[]? ReadCapped(Stream stream, long maxBytes, Action? beforeRead = null)
    {
        using var content = new MemoryStream();
        var buffer = new byte[ChunkBytes];
        while (true)
        {
            beforeRead?.Invoke();
            var n = stream.Read(buffer, 0, buffer.Length);
            if (n == 0) return content.ToArray();
            if (content.Length + n > maxBytes) return null;
            content.Write(buffer, 0, n);
        }
    }

    // a read of the clipboard's formats, each absent when null or empty; nothing at all is Empty
    internal static ClipboardRead Read(byte[]? text, byte[]? png, byte[]? html, byte[]? rtf, bool concealed)
    {
        text = NonEmpty(text);
        png = NonEmpty(png);
        html = NonEmpty(html);
        rtf = NonEmpty(rtf);
        if (text == null && png == null && html == null && rtf == null) return ClipboardRead.Empty;
        return new ClipboardRead(
            text != null ? Encoding.UTF8.GetString(text) : null,
            png,
            html != null ? Encoding.UTF8.GetString(html) : null,
            rtf,
            concealed);
    }

    private static byte[]? NonEmpty(byte[]? data) => data is { Length: > 0 } ? data : null;
}
