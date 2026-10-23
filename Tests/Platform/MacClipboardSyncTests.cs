using System.Runtime.Versioning;
using Hydra.Platform;
using Hydra.Platform.MacOs;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Platform;

// a private named pasteboard, so the user's clipboard is left alone
[TestFixture]
[SupportedOSPlatform("macos")]
public class MacClipboardSyncTests : CopiedPrimaryContract
{
    // one name, since a named pasteboard outlives the run
    private const string PasteboardName = "org.c-net.hydra.tests";

    protected override IClipboardSync Create() => new MacClipboardSync(NullLogger<MacClipboardSync>.Instance, PasteboardName);

    [TestCase("org.nspasteboard.ConcealedType")]
    [TestCase("org.nspasteboard.TransientType")]
    [TestCase("public.png")]
    [TestCase("public.file-url")]
    public void CopyMarkedAlongside_IsNotThePrimary(string marker)
    {
        HydraWritesThen(() => WriteForeignCopy("secret or name", marker));

        Assert.That(Hydra.GetPrimaryText(), Is.EqualTo("relayed selection"));
    }

    [Test]
    public void TextCopyWithATiffBeside_IsThePrimary()
    {
        // a TIFF is not read as an image, so the copy travels as text and is the primary too
        HydraWritesThen(() => WriteForeignCopy("copied", "public.tiff"));

        Assert.That(Hydra.GetPrimaryText(), Is.EqualTo("copied"));
    }

    [Test]
    public void Clear_MarksThePlaceholderTransient_ButNotConcealed()
    {
        Hydra.ClearClipboard();

        var types = Types();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(types, Does.Contain("org.nspasteboard.TransientType"));
            Assert.That(types, Does.Not.Contain("org.nspasteboard.ConcealedType"));
        }
    }

    [Test]
    public void ReadAll_ACopyDuringTheRead_ReadsTheNewCopyWhole()
    {
        WriteForeignCopy("username", "public.html");
        var sync = (MacClipboardSync)Hydra;
        var copies = 0;
        sync.DuringRead = () =>
        {
            if (copies++ == 0) WriteForeignCopy("password", "org.nspasteboard.ConcealedType");
        };

        var read = sync.ReadAll();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(read.Text, Is.EqualTo("password"));
            Assert.That(read.Concealed, Is.True);
        }
    }

    [Test]
    public void ReadAll_CopiesDuringEveryRead_IsUnreadable()
    {
        WriteForeignCopy("username", "public.html");
        var sync = (MacClipboardSync)Hydra;
        sync.DuringRead = () => WriteForeignCopy("password", "org.nspasteboard.ConcealedType");

        Assert.Throws<ClipboardUnreadableException>(() => sync.ReadAll());
    }

    [Test]
    public void ReadAll_AConcealedCopy_IsConcealed_AndThePlaceholderReadsAsNoCopy()
    {
        WriteForeignCopy("secret", "org.nspasteboard.ConcealedType");
        var concealed = Hydra.ReadAll();
        Hydra.ClearClipboard();
        var placeholder = ClipboardUtils.ReadWithFallback(Hydra, null, NullLogger.Instance, "test");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(concealed.Text, Is.EqualTo("secret"));
            Assert.That(concealed.Concealed, Is.True);
            Assert.That(placeholder, Is.EqualTo(ClipboardSnapshot.Empty));
        }
    }

    [Test]
    public void SetClipboard_Concealed_WritesBothMarkers()
    {
        var written = Hydra.SetClipboard(new ClipboardSnapshot("secret", null, null, Concealed: true));

        var types = Types();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(written, Is.EqualTo(new ClipboardSnapshot("secret", null, null, Concealed: true)));
            Assert.That(types, Does.Contain("org.nspasteboard.ConcealedType"));
            Assert.That(types, Does.Contain("org.nspasteboard.TransientType"));
        }
    }

    // the pasteboard's types, as another app sees them
    private static List<string?> Types()
    {
        using var pool = new ObjcAutoreleasePool();
        var types = NativeMethods.objc_msgSend_noarg(MacClipboardSync.Pasteboard(PasteboardName), NativeMethods.sel_registerName("types"));
        var count = NativeMethods.objc_msgSend_long(types, NativeMethods.sel_registerName("count"));
        var at = NativeMethods.sel_registerName("objectAtIndex:");
        return [.. Enumerable.Range(0, (int)count).Select(i => NativeHelpers.CfStringToManaged(NativeMethods.objc_msgSend_nuint(types, at, (nuint)i)))];
    }

    // another app's copy: its text, and an empty marker type beside it
    private static void WriteForeignCopy(string text, string markerType)
    {
        using var pool = new ObjcAutoreleasePool();
        var pasteboard = MacClipboardSync.Pasteboard(PasteboardName);
        _ = NativeMethods.objc_msgSend_long(pasteboard, NativeMethods.sel_registerName("clearContents"));
        MacClipboardSync.WriteString(pasteboard, text, "public.utf8-plain-text");
        MacClipboardSync.WriteString(pasteboard, "", markerType);
    }
}
