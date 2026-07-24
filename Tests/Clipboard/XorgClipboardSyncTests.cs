using System.Diagnostics;
using System.Text;
using Hydra.Platform;
using Hydra.Platform.Linux;

namespace Tests.Clipboard;

// End-to-end X11 selection-protocol tests for XorgClipboardSync. These drive the real
// Xlib code against a live X server (Xvfb) and use xclip as the peer so we can assert the
// exact wire bytes — in particular that STRING is ISO-8859-1 while UTF8_STRING is UTF-8.
//
// They self-skip unless on Linux with a live DISPLAY (see RequireX11), so libX11 never loads on
// macOS/Windows and a plain `dotnet test` is safe everywhere. They actually execute under Xvfb:
// the test-linux CI job (dotnet test under xvfb-run) and run-tests.sh linux. Sequential by design:
// the CLIPBOARD selection is a single global resource.
[TestFixture]
[Category("Linux")]
[NonParallelizable]
public class XorgClipboardSyncTests
{
    // discovered everywhere but only runnable on Linux with a live X server. Skip (don't fail) elsewhere,
    // and skip BEFORE touching XorgClipboardSync so libX11 is never loaded on macOS/Windows.
    [OneTimeSetUp]
    public void RequireX11()
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            Assert.Ignore("Requires Linux with an X server (run ./run-tests.sh linux)");
    }

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
        owner.SetText("café");

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
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5 };
        using var owner = new XorgClipboardSync();
        owner.SetImagePng(png);

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
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 9, 8, 7, 6 };
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

    // -- xclip helpers (raw bytes in/out) --

    private static byte[] XclipRead(string target)
    {
        using var p = Process.Start(new ProcessStartInfo("xclip", $"-selection clipboard -o -t {target}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        using var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);
        p.WaitForExit(5000);
        return ms.ToArray();
    }

    private static void XclipWrite(string target, byte[] data)
    {
        using var p = Process.Start(new ProcessStartInfo("xclip", $"-selection clipboard -i -t {target}")
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
        })!;
        p.StandardInput.BaseStream.Write(data);
        p.StandardInput.BaseStream.Close();
        p.WaitForExit(5000);
    }
}
