using Hydra.Platform;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.Clipboard;

// covers the Html/Rtf rich-format behavior added to ClipboardUtils.
[TestFixture]
public class ClipboardUtilsRichTests
{
    private static readonly NullLogger Log = NullLogger.Instance;
    private static byte[] Oversized() => new byte[ClipboardUtils.MaxClipboardBytes + 1];
    private static string OversizedText() => new('a', (int)ClipboardUtils.MaxClipboardBytes + 1);

    // -- ClipboardHash: rich fields participate --

    [Test]
    public void ClipboardHash_DiffersWhenHtmlDiffers()
    {
        var a = new ClipboardSnapshot("t", null, null, Html: "<b>a</b>");
        var b = new ClipboardSnapshot("t", null, null, Html: "<b>b</b>");
        Assert.That(ClipboardUtils.ClipboardHash(a), Is.Not.EqualTo(ClipboardUtils.ClipboardHash(b)));
    }

    [Test]
    public void ClipboardHash_DiffersWhenRtfDiffers()
    {
        var a = new ClipboardSnapshot("t", null, null, Rtf: [1, 2, 3]);
        var b = new ClipboardSnapshot("t", null, null, Rtf: [1, 2, 4]);
        Assert.That(ClipboardUtils.ClipboardHash(a), Is.Not.EqualTo(ClipboardUtils.ClipboardHash(b)));
    }

    [Test]
    public void ClipboardHash_StableForSameContent()
    {
        var a = new ClipboardSnapshot("t", "p", [9], Html: "<i>x</i>", Rtf: [7]);
        var b = new ClipboardSnapshot("t", "p", [9], Html: "<i>x</i>", Rtf: [7]);
        Assert.That(ClipboardUtils.ClipboardHash(a), Is.EqualTo(ClipboardUtils.ClipboardHash(b)));
    }

    // -- TrimToFit: plain text is the last thing dropped --

    [Test]
    public void TrimToFit_DropsOversizedHtml_KeepsText()
    {
        var result = ClipboardUtils.TrimToFit("keep me", null, null, OversizedText(), null, Log, "test");
        Assert.Multiple(() =>
        {
            Assert.That(result.Text, Is.EqualTo("keep me"));
            Assert.That(result.Html, Is.Null);
        });
    }

    [Test]
    public void TrimToFit_DropsOversizedRtf_KeepsText()
    {
        var result = ClipboardUtils.TrimToFit("keep me", null, null, null, Oversized(), Log, "test");
        Assert.Multiple(() =>
        {
            Assert.That(result.Text, Is.EqualTo("keep me"));
            Assert.That(result.Rtf, Is.Null);
        });
    }

    [Test]
    public void TrimToFit_KeepsAllWhenUnderBudget()
    {
        var result = ClipboardUtils.TrimToFit("t", "p", [1], "<b>x</b>", [2], Log, "test");
        Assert.Multiple(() =>
        {
            Assert.That(result.Text, Is.EqualTo("t"));
            Assert.That(result.Html, Is.EqualTo("<b>x</b>"));
            Assert.That(result.Rtf, Is.EqualTo(new byte[] { 2 }));
        });
    }

    // -- ValidateFields: oversized rich fields are dropped, valid ones pass --

    [Test]
    public void ValidateFields_DropsOversizedHtmlAndRtf_KeepsValid()
    {
        var result = ClipboardUtils.ValidateFields("t", null, null, OversizedText(), Oversized(), Log, "test", "host");
        Assert.Multiple(() =>
        {
            Assert.That(result.Text, Is.EqualTo("t"));
            Assert.That(result.Html, Is.Null);
            Assert.That(result.Rtf, Is.Null);
        });
    }

    [Test]
    public void ValidateFields_KeepsValidRich()
    {
        var result = ClipboardUtils.ValidateFields("t", null, null, "<b>x</b>", [3], Log, "test", "host");
        Assert.Multiple(() =>
        {
            Assert.That(result.Html, Is.EqualTo("<b>x</b>"));
            Assert.That(result.Rtf, Is.EqualTo(new byte[] { 3 }));
        });
    }

    // -- ReadWithFallback: rich reps travel with text; image copy still wins --

    [Test]
    public void ReadWithFallback_CarriesHtmlAndRtf()
    {
        var sync = new FakeClipboardSync();
        sync.SetClipboard(new ClipboardSnapshot("plain", null, null, Html: "<b>rich</b>", Rtf: [5, 6]));
        var result = ClipboardUtils.ReadWithFallback(sync, null, Log, "read");
        Assert.Multiple(() =>
        {
            Assert.That(result.Text, Is.EqualTo("plain"));
            Assert.That(result.Html, Is.EqualTo("<b>rich</b>"));
            Assert.That(result.Rtf, Is.EqualTo(new byte[] { 5, 6 }));
        });
    }

    [Test]
    public void ReadWithFallback_ImageCopy_DropsRichAndText()
    {
        var sync = new FakeClipboardSync();
        // image present alongside html: an image copy wins outright, rich/text are not carried
        sync.SetClipboard(new ClipboardSnapshot("t", null, [9, 9], Html: "<b>x</b>"));
        var result = ClipboardUtils.ReadWithFallback(sync, null, Log, "read");
        Assert.Multiple(() =>
        {
            Assert.That(result.ImagePng, Is.EqualTo(new byte[] { 9, 9 }));
            Assert.That(result.Html, Is.Null);
            Assert.That(result.Text, Is.Null);
        });
    }

    [Test]
    public void ReadWithFallback_UsesFallbackWhenEverythingEchoSuppressed()
    {
        var sync = new FakeClipboardSync(); // all getters return null
        var fallback = new ClipboardSnapshot("ft", null, null, Html: "<i>fh</i>", Rtf: [1]);
        var result = ClipboardUtils.ReadWithFallback(sync, fallback, Log, "read");
        Assert.Multiple(() =>
        {
            Assert.That(result.Text, Is.EqualTo("ft"));
            Assert.That(result.Html, Is.EqualTo("<i>fh</i>"));
            Assert.That(result.Rtf, Is.EqualTo(new byte[] { 1 }));
        });
    }
}
