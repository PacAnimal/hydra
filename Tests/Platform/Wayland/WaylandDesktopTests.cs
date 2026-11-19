using Hydra.Platform.Linux.Wayland;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.Platform.Wayland;

[TestFixture]
public class WaylandDesktopTests
{
    private const string Exe = "/home/user/.local/bin/hydra";

    [Test]
    public void Entry_IsAHiddenApplicationNamingTheExecutable()
    {
        var entry = WaylandDesktop.DesktopEntry(Exe);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entry, Does.StartWith("[Desktop Entry]\n"));
            Assert.That(entry, Does.Contain("\nType=Application\n"));
            Assert.That(entry, Does.Contain("\nName=Hydra\n"));
            Assert.That(entry, Does.Contain("\nComment="));
            Assert.That(entry, Does.Contain("\nNoDisplay=true\n"));
            Assert.That(WaylandDesktop.ExecOf(entry), Is.EqualTo(Exe));
            Assert.That(entry, Does.Not.Contain("\r"));
        }
    }

    [Test]
    public void Missing_IsWritten()
    {
        Assert.That(WaylandDesktop.NeedsWrite(null, Exe), Is.True);
    }

    [Test]
    public void OurOwn_IsLeftAlone()
    {
        Assert.That(WaylandDesktop.NeedsWrite(WaylandDesktop.DesktopEntry(Exe), Exe), Is.False);
    }

    [Test]
    public void AnEntryFromAMovedExecutable_IsRewritten()
    {
        Assert.That(WaylandDesktop.NeedsWrite(WaylandDesktop.DesktopEntry("/opt/hydra/hydra"), Exe), Is.True);
    }

    [Test]
    public void AnEntryWithNoExec_IsRewritten()
    {
        Assert.That(WaylandDesktop.NeedsWrite("[Desktop Entry]\nName=Hydra\n", Exe), Is.True);
    }

    [Test]
    public void ExecOf_ReadsOnlyTheDesktopEntryGroup()
    {
        const string entry = "[Desktop Action other]\nExec=/bin/false\n\n[Desktop Entry]\r\nType=Application\r\nExec=/usr/bin/hydra\r\n";

        Assert.That(WaylandDesktop.ExecOf(entry), Is.EqualTo("/usr/bin/hydra"));
    }

    [Test]
    public void APlainPath_IsNotQuoted()
    {
        Assert.That(WaylandDesktop.QuoteExec(Exe), Is.EqualTo(Exe));
    }

    [Test]
    public void APathWithASpace_IsQuoted()
    {
        Assert.That(WaylandDesktop.QuoteExec("/home/user/My Apps/hydra"), Is.EqualTo("\"/home/user/My Apps/hydra\""));
    }

    [Test]
    public void ReservedCharacters_AreEscapedAsTheSpecSays()
    {
        // $ inside quotes takes a backslash, which the string escape rule then doubles
        Assert.That(WaylandDesktop.QuoteExec("/opt/$hydra"), Is.EqualTo("\"/opt/\\\\$hydra\""));
    }

    [Test]
    public void AQuotedPath_RoundTripsWithoutARewrite()
    {
        const string spaced = "/home/user/My Apps/hydra";

        Assert.That(WaylandDesktop.NeedsWrite(WaylandDesktop.DesktopEntry(spaced), spaced), Is.False);
    }

    [Test]
    public void Ensure_AMissingEntry_IsWrittenAndSaysSo()
    {
        var path = EntryPath();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(WaylandDesktop.EnsureDesktopEntry(path, Exe, NullLogger.Instance), Is.True);
            Assert.That(File.ReadAllText(path), Is.EqualTo(WaylandDesktop.DesktopEntry(Exe)));
        }
    }

    [Test]
    public void Ensure_OurOwnEntry_IsLeftAloneAndSaysSo()
    {
        var path = EntryPath();
        WaylandDesktop.EnsureDesktopEntry(path, Exe, NullLogger.Instance);

        Assert.That(WaylandDesktop.EnsureDesktopEntry(path, Exe, NullLogger.Instance), Is.False);
    }

    [Test]
    public void Ensure_AnEntryFromAMovedExecutable_IsRewrittenAndSaysSo()
    {
        var path = EntryPath();
        WaylandDesktop.EnsureDesktopEntry(path, "/opt/hydra/hydra", NullLogger.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(WaylandDesktop.EnsureDesktopEntry(path, Exe, NullLogger.Instance), Is.True);
            Assert.That(WaylandDesktop.ExecOf(File.ReadAllText(path)), Is.EqualTo(Exe));
        }
    }

    [Test]
    public void Ensure_AnEntryThatCannotBeWritten_SaysNothingWasWritten()
    {
        // the applications directory is taken by a file, so the entry's directory cannot be made
        var root = TestPaths.FreshFixtureRoot(nameof(WaylandDesktopTests));
        var blocker = Path.Combine(root, "applications");
        File.WriteAllText(blocker, "");

        Assert.That(WaylandDesktop.EnsureDesktopEntry(Path.Combine(blocker, "org.cathedral.Hydra.desktop"), Exe, NullLogger.Instance), Is.False);
    }

    private static string EntryPath() =>
        Path.Combine(TestPaths.FreshFixtureRoot(nameof(WaylandDesktopTests)), "applications", "org.cathedral.Hydra.desktop");
}
