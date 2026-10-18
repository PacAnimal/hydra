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
