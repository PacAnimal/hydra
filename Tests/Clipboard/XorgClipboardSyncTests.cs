using System.Diagnostics;
using System.Text;
using Hydra.Platform;
using Hydra.Platform.Linux;
using Tests.Setup;

namespace Tests.Clipboard;

// the real Xlib code against Xvfb, with xclip as the peer; sequential, since CLIPBOARD is global
[TestFixture]
[Category("Linux")]
[NonParallelizable]
public class XorgClipboardSyncTests
{
    [OneTimeSetUp]
    public void RequireX11() => X11Required.OrIgnore();

    [TearDown]
    public void KillStrayXclip()
    {
        // xclip daemonises to serve a selection it owns; drop any leftover so the next test starts clean
        try { Process.Start(new ProcessStartInfo("pkill", "-x xclip") { RedirectStandardError = true })?.WaitForExit(2000); }
        catch { /* pkill may be absent; a new owner evicts the old xclip anyway */ }
    }

    // -- serve side: our instance owns the selection, xclip reads a specific target --

    [Test]
    public void Serve_String_IsLatin1_Utf8String_IsUtf8()
    {
        using var owner = new XorgClipboardSync();
        owner.SetClipboard(new ClipboardSnapshot("café", null, null));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(XclipRead("STRING"), Is.EqualTo(new byte[] { 0x63, 0x61, 0x66, 0xE9 }), "STRING must be ISO-8859-1");
            Assert.That(XclipRead("UTF8_STRING"), Is.EqualTo(Encoding.UTF8.GetBytes("café")), "UTF8_STRING must be UTF-8");
        }
    }

    [Test]
    public void Serve_Html_AndText()
    {
        using var owner = new XorgClipboardSync();
        owner.SetClipboard(new ClipboardSnapshot("café", null, null, Html: "<b>café</b>"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(XclipRead("text/html"), Is.EqualTo(Encoding.UTF8.GetBytes("<b>café</b>")));
            Assert.That(XclipRead("UTF8_STRING"), Is.EqualTo(Encoding.UTF8.GetBytes("café")));
        }
    }

    [Test]
    public void Serve_ImagePng()
    {
        var png = TestImages.Png;
        using var owner = new XorgClipboardSync();
        owner.SetClipboard(new ClipboardSnapshot(null, null, png));

        Assert.That(XclipRead("image/png"), Is.EqualTo(png));
    }

    // -- read side: xclip owns the selection, our instance reads it back --

    [Test]
    public void Read_Utf8Text_FromForeignOwner()
    {
        const string s = "café ☕ 日本語";
        XclipWrite("UTF8_STRING", Encoding.UTF8.GetBytes(s));

        using var reader = new XorgClipboardSync();
        Assert.That(reader.GetText(), Is.EqualTo(s));
    }

    [Test]
    public void Read_Html_FromForeignOwner()
    {
        XclipWrite("text/html", Encoding.UTF8.GetBytes("<i>café</i>"));

        using var reader = new XorgClipboardSync();
        Assert.That(reader.GetHtml(), Is.EqualTo("<i>café</i>"));
    }

    [Test]
    public void Read_ImagePng_FromForeignOwner()
    {
        var png = TestImages.Png;
        XclipWrite("image/png", png);

        using var reader = new XorgClipboardSync();
        Assert.That(reader.GetImagePng(), Is.EqualTo(png));
    }

    // -- round-trip between two of our own instances (no external peer) --

    [Test]
    public void RoundTrip_HtmlAndText_BetweenInstances()
    {
        using var owner = new XorgClipboardSync();
        using var reader = new XorgClipboardSync();
        owner.SetClipboard(new ClipboardSnapshot("plain", null, null, Html: "<b>rich</b>"));

        // barrier: block until the owner actually serves the selection to a third connection,
        // which proves ownership has propagated server-side before the C# reader converts it
        XclipRead("UTF8_STRING");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.GetText(), Is.EqualTo("plain"));
            Assert.That(reader.GetHtml(), Is.EqualTo("<b>rich</b>"));
        }
    }

    // -- PRIMARY: a selection of its own, read as it stands even when Hydra set it --

    [Test]
    public void Primary_SetByHydra_ReadsBackAsIs()
    {
        using var owner = new XorgClipboardSync();
        owner.SetPrimaryText("selected");

        Assert.That(owner.GetPrimaryText(), Is.EqualTo("selected"));
    }

    [Test]
    public void Primary_FromForeignOwner()
    {
        using var reader = new XorgClipboardSync();
        XclipWrite("UTF8_STRING", Encoding.UTF8.GetBytes("highlighted"), "primary");

        Assert.That(reader.GetPrimaryText(), Is.EqualTo("highlighted"));
    }

    [Test]
    public void Primary_AlreadyHeldElsewhere_IsLeftWithItsOwner()
    {
        // taking PRIMARY back with the same text would clear the owner's highlight
        using var owner = new XorgClipboardSync();
        using var receiver = new XorgClipboardSync();
        owner.SetPrimaryText("highlighted");
        XclipRead("UTF8_STRING", "primary"); // barrier: the server has the owner

        receiver.SetPrimaryText("highlighted");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(owner.OwnsPrimary, Is.True);
            Assert.That(receiver.OwnsPrimary, Is.False);
        }
    }

    [Test]
    public void SetClipboard_ServesClipboardAndPrimarySeparately()
    {
        using var owner = new XorgClipboardSync();
        owner.SetClipboard(new ClipboardSnapshot("copied", "selected", null));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(XclipRead("UTF8_STRING"), Is.EqualTo(Encoding.UTF8.GetBytes("copied")));
            Assert.That(XclipRead("UTF8_STRING", "primary"), Is.EqualTo(Encoding.UTF8.GetBytes("selected")));
        }
    }

    // -- xclip helpers (raw bytes in/out) --

    private static byte[] XclipRead(string target, string selection = "clipboard")
    {
        using var p = Process.Start(new ProcessStartInfo("xclip", $"-selection {selection} -o -t {target}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        using var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);
        p.WaitForExit(5000);
        return ms.ToArray();
    }

    private static void XclipWrite(string target, byte[] data, string selection = "clipboard")
    {
        using var p = Process.Start(new ProcessStartInfo("xclip", $"-selection {selection} -i -t {target}")
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
        })!;
        p.StandardInput.BaseStream.Write(data);
        p.StandardInput.BaseStream.Close();
        p.WaitForExit(5000);
    }
}
