using System.Text;
using Hydra.Platform.Windows;

namespace Tests.Clipboard;

// CF_HTML wrap/unwrap is pure byte-offset logic (no Win32), so it is fully testable on any OS.
[TestFixture]
public class CfHtmlTests
{
    // -- helpers to read back the header the way Windows apps do --

    private static int Offset(byte[] blob, string keyword)
    {
        var header = Encoding.ASCII.GetString(blob, 0, Math.Min(blob.Length, 1024));
        var i = header.IndexOf(keyword, StringComparison.Ordinal) + keyword.Length;
        var j = i;
        while (j < header.Length && (char.IsAsciiDigit(header[j]) || (header[j] == '-' && j == i))) j++;
        return int.Parse(header.AsSpan(i, j - i));
    }

    private static string Slice(byte[] blob, int start, int end) => Encoding.UTF8.GetString(blob, start, end - start);

    // -- Wrap: offset correctness --

    [Test]
    public void Wrap_FragmentOffsets_PointAtExactFragmentBytes()
    {
        const string html = "<b>hi</b>";
        var blob = CfHtml.Wrap(html);

        var startFragment = Offset(blob, "StartFragment:");
        var endFragment = Offset(blob, "EndFragment:");
        // the bytes between StartFragment and EndFragment must be exactly the raw fragment
        Assert.That(Slice(blob, startFragment, endFragment), Is.EqualTo(html));
    }

    [Test]
    public void Wrap_ContextOffsets_SpanTheWholeHtmlDocument()
    {
        var blob = CfHtml.Wrap("<b>hi</b>");
        var startHtml = Offset(blob, "StartHTML:");
        var endHtml = Offset(blob, "EndHTML:");

        var context = Slice(blob, startHtml, endHtml);
        Assert.That(context, Does.StartWith("<html>"));
        Assert.That(context, Does.EndWith("</html>"));
        Assert.That(context, Does.Contain("<!--StartFragment-->"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(context, Does.Contain("<b>hi</b>"));
            Assert.That(endHtml, Is.EqualTo(blob.Length)); // context runs to the end of the blob
        }
    }

    [Test]
    public void Wrap_OffsetsAreAscendingAndInBounds()
    {
        var blob = CfHtml.Wrap("<p>x</p>");
        int sh = Offset(blob, "StartHTML:"), sf = Offset(blob, "StartFragment:"),
            ef = Offset(blob, "EndFragment:"), eh = Offset(blob, "EndHTML:");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sh, Is.LessThan(sf));
            Assert.That(sf, Is.LessThan(ef));
            Assert.That(ef, Is.LessThanOrEqualTo(eh));
            Assert.That(eh, Is.LessThanOrEqualTo(blob.Length));
        }
    }

    [Test]
    public void Wrap_Utf8Multibyte_ByteOffsetsAccountForEncoding()
    {
        // é (2 bytes), 中 (3 bytes), 🎉 (4 bytes) — char offsets would be wrong; byte offsets must be right
        const string html = "<p>café 中 🎉</p>";
        var blob = CfHtml.Wrap(html);
        var sf = Offset(blob, "StartFragment:");
        var ef = Offset(blob, "EndFragment:");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Slice(blob, sf, ef), Is.EqualTo(html));
            Assert.That(ef - sf, Is.EqualTo(Encoding.UTF8.GetByteCount(html)));
        }
    }

    [Test]
    public void Wrap_HeaderLengthIsConstant_RegardlessOfContentSize()
    {
        var small = CfHtml.Wrap("<b>x</b>");
        var large = CfHtml.Wrap(new string('a', 100_000));
        // StartHTML == header length; fixed-width offsets keep it identical for any payload
        Assert.That(Offset(small, "StartHTML:"), Is.EqualTo(Offset(large, "StartHTML:")));
    }

    // -- Unwrap: real samples & round-trips --

    [Test]
    public void Unwrap_RoundTripsWrappedHtml()
    {
        const string html = "<div><p>Hello <b>world</b></p></div>";
        var recovered = CfHtml.Unwrap(CfHtml.Wrap(html));
        Assert.That(recovered, Is.Not.Null);
        Assert.That(recovered, Does.Contain(html)); // context wraps the fragment verbatim
    }

    [Test]
    public void Unwrap_Utf8RoundTrip_Preserved()
    {
        const string html = "<p>café 中 🎉</p>";
        var recovered = CfHtml.Unwrap(CfHtml.Wrap(html));
        Assert.That(recovered, Does.Contain(html));
    }

    [Test]
    public void Unwrap_RealWindowsSample_ExtractsContextDocument()
    {
        const string body = "<html><body><!--StartFragment--><b>Bold</b><!--EndFragment--></body></html>";
        static string H(int a, int b, int c, int d) =>
            $"Version:1.0\r\nStartHTML:{a:D10}\r\nEndHTML:{b:D10}\r\nStartFragment:{c:D10}\r\nEndFragment:{d:D10}\r\n";
        var hlen = H(0, 0, 0, 0).Length;
        var sf = hlen + "<html><body><!--StartFragment-->".Length;
        var ef = sf + "<b>Bold</b>".Length;
        var blob = Encoding.UTF8.GetBytes(H(hlen, hlen + body.Length, sf, ef) + body);

        Assert.That(CfHtml.Unwrap(blob), Is.EqualTo(body));
    }

    [Test]
    public void Unwrap_FragmentOnly_WhenNoContext()
    {
        // StartHTML/EndHTML == -1 → fall back to the fragment range
        const string fragment = "<i>frag</i>";
        var hdr = "Version:1.0\r\nStartHTML:-000000001\r\nEndHTML:-000000001\r\nStartFragment:{0}\r\nEndFragment:{1}\r\n";
        var hlen = string.Format(hdr, "0000000000", "0000000000").Length;
        var ef = hlen + Encoding.UTF8.GetByteCount(fragment);
        var blob = Encoding.UTF8.GetBytes(string.Format(hdr, hlen.ToString("D10"), ef.ToString("D10")) + fragment);

        Assert.That(CfHtml.Unwrap(blob), Is.EqualTo(fragment));
    }

    [Test]
    public void Unwrap_ToleratesLfLineEndings()
    {
        // some producers use \n instead of \r\n in the header
        var body = "<html><body><!--StartFragment--><b>x</b><!--EndFragment--></body></html>";
        static string H(int a, int b, int c, int d) =>
            $"Version:1.0\nStartHTML:{a:D10}\nEndHTML:{b:D10}\nStartFragment:{c:D10}\nEndFragment:{d:D10}\n";
        var hlen = H(0, 0, 0, 0).Length;
        var sf = hlen + "<html><body><!--StartFragment-->".Length;
        var ef = sf + "<b>x</b>".Length;
        var blob = Encoding.UTF8.GetBytes(H(hlen, hlen + body.Length, sf, ef) + body);
        Assert.That(CfHtml.Unwrap(blob), Does.Contain("<b>x</b>"));
    }

    // -- malformed inputs never throw, return null --

    [TestCase(new byte[0])]
    [TestCase(new byte[] { 1, 2, 3 })]
    public void Unwrap_Malformed_ReturnsNull(byte[] input) =>
        Assert.That(CfHtml.Unwrap(input), Is.Null);

    [Test]
    public void Unwrap_NoHeaderKeywords_ReturnsNull() =>
        Assert.That(CfHtml.Unwrap(Encoding.UTF8.GetBytes("<html>just html, no header</html>")), Is.Null);

    [Test]
    public void Unwrap_OffsetsOutOfRange_ReturnsNull()
    {
        var blob = Encoding.UTF8.GetBytes(
            "Version:1.0\r\nStartHTML:0000000097\r\nEndHTML:0009999999\r\nStartFragment:0000000131\r\nEndFragment:0009999999\r\n<html></html>");
        Assert.That(CfHtml.Unwrap(blob), Is.Null); // end offsets exceed blob length
    }

    [Test]
    public void Unwrap_Null_ReturnsNull() => Assert.That(CfHtml.Unwrap(null!), Is.Null);

    [Test]
    public void Unwrap_ToleratesSpaceAfterColon()
    {
        // the spec's "Offset syntax" shows "StartHTML: 0000000097" (space after colon)
        const string body = "<html><body><!--StartFragment--><b>x</b><!--EndFragment--></body></html>";
        static string H(int a, int b, int c, int d) =>
            $"Version:1.0\r\nStartHTML: {a:D10}\r\nEndHTML: {b:D10}\r\nStartFragment: {c:D10}\r\nEndFragment: {d:D10}\r\n";
        var hlen = H(0, 0, 0, 0).Length;
        var sf = hlen + "<html><body><!--StartFragment-->".Length;
        var ef = sf + "<b>x</b>".Length;
        var blob = Encoding.UTF8.GetBytes(H(hlen, hlen + body.Length, sf, ef) + body);
        Assert.That(CfHtml.Unwrap(blob), Is.EqualTo(body));
    }

    [Test]
    public void Unwrap_ToleratesLoneCrLineEndings()
    {
        const string body = "<html><body><!--StartFragment--><b>x</b><!--EndFragment--></body></html>";
        static string H(int a, int b, int c, int d) =>
            $"Version:0.9\rStartHTML:{a:D10}\rEndHTML:{b:D10}\rStartFragment:{c:D10}\rEndFragment:{d:D10}\r";
        var hlen = H(0, 0, 0, 0).Length;
        var sf = hlen + "<html><body><!--StartFragment-->".Length;
        var ef = sf + "<b>x</b>".Length;
        var blob = Encoding.UTF8.GetBytes(H(hlen, hlen + body.Length, sf, ef) + body);
        Assert.That(CfHtml.Unwrap(blob), Is.EqualTo(body)); // lone-CR + Version:0.9 both accepted
    }

    [Test]
    public void Wrap_EmptyHtml_ProducesEmptyFragment()
    {
        var blob = CfHtml.Wrap("");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Offset(blob, "StartFragment:"), Is.EqualTo(Offset(blob, "EndFragment:"))); // empty fragment
            Assert.That(CfHtml.Unwrap(blob), Does.Contain("<!--StartFragment--><!--EndFragment-->"));
        }
    }
}
