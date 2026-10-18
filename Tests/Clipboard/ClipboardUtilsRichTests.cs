using System.Runtime.CompilerServices;
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

    // -- ClipboardHash: rich fields participate, the selection beside them does not --

    [Test]
    public void EchoFilter_ForgetsWhatALaterWriteReplaced()
    {
        // a write replaces every format, so an image Hydra wrote before a text is no echo once the user copies it again
        var echo = new ClipboardEchoFilter(() => true);
        echo.Track(new ClipboardSnapshot(null, null, [9, 9]));
        echo.Track(new ClipboardSnapshot("written", null, null));

        Assert.That(echo.IsDuplicateImage([9, 9]), Is.False);
    }

    [Test]
    public void EchoFilter_PassesWhatItWroteOnceTheClipboardIsNoLongerItsWrite()
    {
        var owned = new StrongBox<bool>(true);
        var echo = new ClipboardEchoFilter(() => owned.Value);
        echo.Track(new ClipboardSnapshot("written", null, [9, 9], "<b>written</b>", [7]));
        Assert.That(echo.FilterText("written"), Is.Null, "an echo while the clipboard is still the write");
        owned.Value = false; // the user copied the same contents again

        using (Assert.EnterMultipleScope())
        {
            Assert.That(echo.FilterText("written"), Is.EqualTo("written"));
            Assert.That(echo.IsDuplicateImage([9, 9]), Is.False);
            Assert.That(echo.FilterHtml("<b>written</b>"), Is.EqualTo("<b>written</b>"));
            Assert.That(echo.FilterRtf([7]), Is.EqualTo(new byte[] { 7 }));
        }
    }

    [Test]
    public void StableHash_IsTheSameOnEveryHost() =>
        // XXH64's published vector for "abc": a per-process seed (System.HashCode) would never match a peer's
        Assert.That(ClipboardUtils.StableHash("abc"), Is.EqualTo(0x44BC2CF5AD770999UL));

    [Test]
    public void ClipboardHash_IgnoresTheSelection() =>
        Assert.That(ClipboardUtils.ClipboardHash(new ClipboardSnapshot("t", "a", null)),
            Is.EqualTo(ClipboardUtils.ClipboardHash(new ClipboardSnapshot("t", "b", null))));

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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Text, Is.EqualTo("keep me"));
            Assert.That(result.Html, Is.Null);
        }
    }

    [Test]
    public void TrimToFit_DropsOversizedRtf_KeepsText()
    {
        var result = ClipboardUtils.TrimToFit("keep me", null, null, null, Oversized(), Log, "test");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Text, Is.EqualTo("keep me"));
            Assert.That(result.Rtf, Is.Null);
        }
    }

    [Test]
    public void TrimToFit_KeepsAllWhenUnderBudget()
    {
        var result = ClipboardUtils.TrimToFit("t", "p", [1], "<b>x</b>", [2], Log, "test");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Text, Is.EqualTo("t"));
            Assert.That(result.Html, Is.EqualTo("<b>x</b>"));
            Assert.That(result.Rtf, Is.EqualTo(new byte[] { 2 }));
        }
    }

    // -- ValidateFields: oversized rich fields are dropped, valid ones pass --

    [Test]
    public void ValidateFields_DropsOversizedHtmlAndRtf_KeepsValid()
    {
        var result = ClipboardUtils.ValidateFields("t", null, null, OversizedText(), Oversized(), Log, "test", "host");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Text, Is.EqualTo("t"));
            Assert.That(result.Html, Is.Null);
            Assert.That(result.Rtf, Is.Null);
        }
    }

    [Test]
    public void ValidateFields_KeepsValidRich()
    {
        var result = ClipboardUtils.ValidateFields("t", null, null, "<b>x</b>", [3], Log, "test", "host");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Html, Is.EqualTo("<b>x</b>"));
            Assert.That(result.Rtf, Is.EqualTo(new byte[] { 3 }));
        }
    }

    // -- ReadWithFallback: rich reps travel with text; image copy still wins --

    [Test]
    public void ReadWithFallback_CarriesHtmlAndRtf()
    {
        var sync = new FakeClipboardSync();
        sync.SetClipboard(new ClipboardSnapshot("plain", null, null, Html: "<b>rich</b>", Rtf: [5, 6]));
        var result = ClipboardUtils.ReadWithFallback(sync, null, Log, "read");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Text, Is.EqualTo("plain"));
            Assert.That(result.Html, Is.EqualTo("<b>rich</b>"));
            Assert.That(result.Rtf, Is.EqualTo(new byte[] { 5, 6 }));
        }
    }

    [Test]
    public void ReadWithFallback_ImageCopy_DropsRichAndText()
    {
        var sync = new FakeClipboardSync();
        // image present alongside html: an image copy wins outright, rich/text are not carried
        sync.SetClipboard(new ClipboardSnapshot("t", null, [9, 9], Html: "<b>x</b>"));
        var result = ClipboardUtils.ReadWithFallback(sync, null, Log, "read");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ImagePng, Is.EqualTo("\t\t"u8.ToArray()));
            Assert.That(result.Html, Is.Null);
            Assert.That(result.Text, Is.Null);
        }
    }

    [Test]
    public void ReadWithFallback_UsesFallbackWhenEverythingEchoSuppressed()
    {
        var sync = new FakeClipboardSync(); // all getters return null
        var fallback = new ClipboardSnapshot("ft", null, null, Html: "<i>fh</i>", Rtf: [1]);
        var result = ClipboardUtils.ReadWithFallback(sync, fallback, Log, "read");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Text, Is.EqualTo("ft"));
            Assert.That(result.Html, Is.EqualTo("<i>fh</i>"));
            Assert.That(result.Rtf, Is.EqualTo(new byte[] { 1 }));
        }
    }

    [Test]
    public void ReadWithFallback_FileClipboard_DoesNotResurrectFallback()
    {
        var sync = new FakeClipboardSync { HasFileClipboardValue = true };
        var fallback = new ClipboardSnapshot("stale", null, null);

        var result = ClipboardUtils.ReadWithFallback(sync, fallback, Log, "read");

        Assert.That(result, Is.EqualTo(new ClipboardSnapshot(null, null, null)));
    }

    [Test]
    public void ReadWithFallback_ExternalOwner_DoesNotResurrectFallback()
    {
        var sync = new FakeClipboardSync { CanUseEchoFallbackValue = false };
        var fallback = new ClipboardSnapshot("stale", null, null);

        var result = ClipboardUtils.ReadWithFallback(sync, fallback, Log, "read");

        Assert.That(result, Is.EqualTo(new ClipboardSnapshot(null, null, null)));
    }

    // -- primary selection: a selection of its own, beside the clipboard --

    [Test]
    public void ReadWithFallback_ImageCopy_KeepsThePrimary()
    {
        var sync = new FakeClipboardSync();
        sync.SetClipboard(new ClipboardSnapshot("alt text", "selection", [9, 9]));

        var result = ClipboardUtils.ReadWithFallback(sync, null, Log, "read");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Text, Is.Null);
            Assert.That(result.PrimaryText, Is.EqualTo("selection"));
            Assert.That(result.ImagePng, Is.EqualTo("\t\t"u8.ToArray()));
        }
    }

    [Test]
    public void ReadWithFallback_EchoedClipboard_KeepsTheCurrentPrimary()
    {
        // the clipboard comes from the fallback, the selection never does
        var sync = new FakeClipboardSync();
        sync.SetPrimaryText("current selection");
        var fallback = new ClipboardSnapshot(null, "older selection", [9, 9]);

        var result = ClipboardUtils.ReadWithFallback(sync, fallback, Log, "read");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.PrimaryText, Is.EqualTo("current selection"));
            Assert.That(result.ImagePng, Is.EqualTo("\t\t"u8.ToArray()));
        }
    }

    [Test]
    public void ReadWithFallback_NoSelection_DoesNotTakeTheFallbacks()
    {
        var sync = new FakeClipboardSync();
        var fallback = new ClipboardSnapshot("echoed", "older selection", null);

        var result = ClipboardUtils.ReadWithFallback(sync, fallback, Log, "read");

        Assert.That(result, Is.EqualTo(new ClipboardSnapshot("echoed", null, null)));
    }

    [Test]
    public void ReadWithFallback_FileClipboard_StillCarriesTheSelection()
    {
        var sync = new FakeClipboardSync { HasFileClipboardValue = true };
        sync.SetPrimaryText("selection");

        var result = ClipboardUtils.ReadWithFallback(sync, null, Log, "read");

        Assert.That(result, Is.EqualTo(new ClipboardSnapshot(null, "selection", null)));
    }

    [Test]
    public void TrimToFit_PrimaryRepeatingTheText_CostsNothing()
    {
        var text = new string('a', (int)(ClipboardUtils.MaxClipboardBytes / 2) + 1);

        var result = ClipboardUtils.TrimToFit(text, text, null, "<b>rich</b>", null, Log, "trim");

        Assert.That(result, Is.EqualTo(new ClipboardSnapshot(text, text, null, Html: "<b>rich</b>")));
    }

    [Test]
    public void TrimToFit_ImageWithALargeSelection_DropsTheSelectionFirst()
    {
        // the copy is what the user asked to move; the selection beside it is the lesser of the two
        var selection = new string('s', (int)(ClipboardUtils.MaxClipboardBytes / 2) + 1);
        var image = new byte[ClipboardUtils.MaxClipboardBytes / 2];

        var result = ClipboardUtils.TrimToFit(null, selection, image, null, null, Log, "trim");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ImagePng, Is.SameAs(image));
            Assert.That(result.PrimaryText, Is.Null);
        }
    }

    [Test]
    public void TrimToFit_OversizedImage_KeepsASelectionThatFits()
    {
        var image = new byte[ClipboardUtils.MaxClipboardBytes + 1];

        var result = ClipboardUtils.TrimToFit(null, "selection", image, null, null, Log, "trim");

        Assert.That(result, Is.EqualTo(new ClipboardSnapshot(null, "selection", null)));
    }

    [Test]
    public void ApplyPreservingFiles_SelectionOnly_LeavesTheClipboardAlone()
    {
        var sync = new FakeClipboardSync();
        sync.SetupText("local copy");

        var applied = ClipboardUtils.ApplyPreservingFiles(sync, new ClipboardSnapshot(null, "selection", null), Log, "test");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(applied, Is.EqualTo(new ClipboardSnapshot(null, "selection", null)));
            Assert.That(sync.Text, Is.EqualTo("local copy"));
            Assert.That(sync.PrimaryText, Is.EqualTo("selection"));
        }
    }

    [Test]
    public void ApplyPreservingFiles_NowhereToKeepASelection_ReportsNothing()
    {
        var sync = new FakeClipboardSync { WriteFailsValue = true, KeepsSelectionValue = false };

        Assert.That(ClipboardUtils.ApplyPreservingFiles(sync, new ClipboardSnapshot("incoming", "selection", null), Log, "test"), Is.Null);
    }

    [Test]
    public void ApplyPreservingFiles_FailedWrite_ReportsOnlyTheSelection()
    {
        var sync = new FakeClipboardSync { WriteFailsValue = true };

        var applied = ClipboardUtils.ApplyPreservingFiles(sync, new ClipboardSnapshot("incoming", "selection", null), Log, "test");

        Assert.That(applied, Is.EqualTo(new ClipboardSnapshot(null, "selection", null)));
    }

    [Test]
    public void ReadWithFallback_FileClipboard_TrimsTheSelection()
    {
        var sync = new FakeClipboardSync { HasFileClipboardValue = true };
        sync.SetPrimaryText(OversizedText());

        Assert.That(ClipboardUtils.ReadWithFallback(sync, null, Log, "read").PrimaryText, Is.Null);
    }

    [Test]
    public void ApplyPreservingFiles_FileClipboard_StillTakesTheSelection()
    {
        var sync = new FakeClipboardSync { HasFileClipboardValue = true };

        var applied = ClipboardUtils.ApplyPreservingFiles(sync, new ClipboardSnapshot("incoming", "selection", null), Log, "test");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(applied, Is.EqualTo(new ClipboardSnapshot(null, "selection", null)), "only the selection was applied");
            Assert.That(sync.SetClipboardCallCount, Is.Zero);
            Assert.That(sync.PrimaryText, Is.EqualTo("selection"));
        }
    }

    [Test]
    public void ApplyPreservingFiles_SelectionOnly_AppliesBesideFiles()
    {
        var sync = new FakeClipboardSync { HasFileClipboardValue = true };

        var applied = ClipboardUtils.ApplyPreservingFiles(sync, new ClipboardSnapshot(null, "selection", null), Log, "test");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(applied, Is.Not.Null);
            Assert.That(sync.PrimaryText, Is.EqualTo("selection"));
        }
    }

    [Test]
    public void ApplyPreservingFiles_FileClipboard_DoesNotOverwrite()
    {
        var sync = new FakeClipboardSync { HasFileClipboardValue = true };

        var applied = ClipboardUtils.ApplyPreservingFiles(sync, new ClipboardSnapshot("incoming", null, null), Log, "test");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(applied, Is.Null);
            Assert.That(sync.SetClipboardCallCount, Is.Zero);
        }
    }
}
