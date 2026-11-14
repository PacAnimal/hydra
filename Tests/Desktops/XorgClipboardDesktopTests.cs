using System.Runtime.InteropServices;
using Hydra.Platform.Linux;

namespace Tests.Desktops;

// XorgClipboardSync reading a GTK 4 app's copy on a real X desktop: GTK answers TIMESTAMP with each request's own time,
// not the time it took the clipboard, which once read as a copy made during every read ("changed while it was read,
// twice")
[Category("Desktops")]
public sealed partial class XorgClipboardDesktopTests
{
    private const string Script = "/usr/local/bin/desktop.sh";

    private Desktop? _desktop;
    private Probe? _probe;
    private string? _display;
    private bool _displaySwapped;

    [SetUp]
    public async Task StartDesktop()
    {
        if (!File.Exists(Script)) Assert.Ignore($"{Script} is not installed; the desktop tests run in the desktop lane's container");
        if (!Desktop.Available(DesktopKind.Openbox)) Assert.Ignore("this lane has no openbox desktop");
        _desktop = await Desktop.Start(DesktopKind.Openbox, "xclip-read");
        _probe = await Probe.Input(_desktop);
        // Xlib reads DISPLAY from the C environment, which Environment.SetEnvironmentVariable leaves alone
        _display = Environment.GetEnvironmentVariable("DISPLAY");
        _ = SetEnv("DISPLAY", _desktop.Env["DISPLAY"], 1);
        _displaySwapped = true;
    }

    [TearDown]
    public async Task StopDesktop()
    {
        if (_displaySwapped)
        {
            if (_display != null) _ = SetEnv("DISPLAY", _display, 1);
            else _ = UnsetEnv("DISPLAY");
            _displaySwapped = false;
        }
        if (_probe != null) await _probe.DisposeAsync();
        if (_desktop != null) await _desktop.DisposeAsync();
    }

    [Test]
    public async Task ReadAll_OfAGtk4Copy_ReadsTheText()
    {
        var text = "gtk-" + Guid.NewGuid();
        await _probe!.Command(new { copy = text }, "copied");
        using var sync = new XorgClipboardSync();

        Assert.That(sync.ReadAll().Text, Is.EqualTo(text));
    }

    [Test]
    public async Task ReadAll_OfAGtk4Copy_MadeAgain_ReadsTheNewText()
    {
        await _probe!.Command(new { copy = "first" }, "copied");
        using var sync = new XorgClipboardSync();
        sync.ReadAll();

        var text = "again-" + Guid.NewGuid();
        await _probe.Command(new { copy = text }, "copied");

        Assert.That(sync.ReadAll().Text, Is.EqualTo(text));
    }

    [LibraryImport("libc", EntryPoint = "setenv", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SetEnv(string name, string value, int overwrite);

    [LibraryImport("libc", EntryPoint = "unsetenv", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int UnsetEnv(string name);
}
