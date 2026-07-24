using System.Text;

namespace Hydra.Platform.Windows;

// Converts between the Windows CF_HTML clipboard format ("HTML Format") and portable raw HTML.
// CF_HTML wraps the HTML in an ASCII description header carrying UTF-8 BYTE offsets
// (StartHTML/EndHTML/StartFragment/EndFragment); the payload is UTF-8.
// Spec: https://learn.microsoft.com/en-us/windows/win32/dataxchg/html-clipboard-format
internal static class CfHtml
{
    private const string Prefix = "<html><body><!--StartFragment-->";
    private const string Suffix = "<!--EndFragment--></body></html>";

    // wraps raw HTML into a CF_HTML blob (UTF-8) with a minimal html/body context and correct byte offsets.
    public static byte[] Wrap(string rawHtml)
    {
        rawHtml ??= "";
        var body = Prefix + rawHtml + Suffix;

        // fixed-width (10-digit) offsets keep the header a constant byte length, sidestepping the circular
        // dependency between the offset values and the length of the header that contains them.
        static string Header(int startHtml, int endHtml, int startFragment, int endFragment) =>
            $"Version:1.0\r\nStartHTML:{startHtml:D10}\r\nEndHTML:{endHtml:D10}\r\n" +
            $"StartFragment:{startFragment:D10}\r\nEndFragment:{endFragment:D10}\r\n";

        var headerLen = Header(0, 0, 0, 0).Length; // ASCII + fixed width → byte length == char length, constant
        var startHtml = headerLen;
        var startFragment = headerLen + Encoding.UTF8.GetByteCount(Prefix);
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(rawHtml);
        var endHtml = headerLen + Encoding.UTF8.GetByteCount(body);

        return Encoding.UTF8.GetBytes(Header(startHtml, endHtml, startFragment, endFragment) + body);
    }

    // extracts portable HTML from a CF_HTML blob: the full context document (StartHTML..EndHTML) when
    // present, else the bare fragment (StartFragment..EndFragment). null if the blob isn't valid CF_HTML.
    public static string? Unwrap(byte[] cfHtml)
    {
        if (cfHtml is not { Length: > 0 }) return null;

        // the description header is ASCII and sits at the very start; scan a bounded prefix for the offsets
        var header = Encoding.ASCII.GetString(cfHtml, 0, Math.Min(cfHtml.Length, 1024));
        var startHtml = ReadOffset(header, "StartHTML:");
        var endHtml = ReadOffset(header, "EndHTML:");
        var startFragment = ReadOffset(header, "StartFragment:");
        var endFragment = ReadOffset(header, "EndFragment:");

        // prefer the complete context document; fall back to the fragment (StartHTML/EndHTML == -1)
        var (start, end) = startHtml >= 0 && endHtml > startHtml
            ? (startHtml, endHtml)
            : (startFragment, endFragment);

        if (start < 0 || end <= start || end > cfHtml.Length) return null;
        return Encoding.UTF8.GetString(cfHtml, start, end - start);
    }

    // reads "<keyword><optional sign><digits>" from the header, returns -1 if absent/unparsable
    private static int ReadOffset(string header, string keyword)
    {
        var idx = header.IndexOf(keyword, StringComparison.Ordinal);
        if (idx < 0) return -1;
        idx += keyword.Length;
        while (idx < header.Length && header[idx] == ' ') idx++; // spec permits "Keyword: 000000097"
        var end = idx;
        while (end < header.Length && (char.IsAsciiDigit(header[end]) || (header[end] == '-' && end == idx)))
            end++;
        return int.TryParse(header.AsSpan(idx, end - idx), out var value) ? value : -1;
    }
}
