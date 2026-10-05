using Hydra.Management;
using Tests.Setup;

namespace Tests.Management;

/// <summary>
/// The config directory's files are never readable by anyone else — not during the write, and not if the
/// machine dies in the middle of one.
/// </summary>
[TestFixture]
public class PrivateFileTests
{
    private string _path = null!;

    [SetUp]
    public void SetUp()
    {
        _path = Path.Combine(TestPaths.FreshFixtureRoot(nameof(PrivateFileTests)), ".hydra-management.json");
    }

    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    [Test]
    public async Task TheFileItWritesIsPrivate()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("unix permissions");

        await PrivateFile.Write(_path, "{\"secret\":\"value\"}", Private, CancellationToken.None);

        // a create: a replace keeps the destination's mode whether or not the handler is given one
        Assert.That(UnixMode.Of(_path), Is.EqualTo(Private),
            "the mode never reached the handler, so the file was written through a temp at the process umask — a complete copy readable by anyone");
    }

    /// <summary>
    /// A file whose own mode has no write bit can still be REPLACED, and stays read-only. 0400 is ordinary
    /// hardening for a config holding secrets, and the rollback of an unconfirmed remote apply writes with
    /// the same mode, so a failure here leaves a remote machine on a candidate nobody confirmed.
    /// </summary>
    [Test]
    public async Task AReadOnlyFileCanStillBeReplacedAndStaysReadOnly()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("unix permissions");

        const UnixFileMode readOnly = UnixFileMode.UserRead;
        await PrivateFile.Write(_path, "{\"first\":true}", readOnly, CancellationToken.None);

        await PrivateFile.Write(_path, "{\"second\":true}", readOnly, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await File.ReadAllTextAsync(_path), Does.Contain("second"), "the replacement never landed");
            Assert.That(UnixMode.Of(_path), Is.EqualTo(readOnly), "the file came back writable");
        }
    }

    [Test]
    public async Task WithoutAModeANewFileIsPrivate()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("unix permissions");

        await PrivateFile.Write(_path, "{\"secret\":\"value\"}", null, CancellationToken.None);

        Assert.That(UnixMode.Of(_path), Is.EqualTo(Private), "a new file took the process umask");
    }

    // an admin's chmod on hydra.conf is theirs to make; a rewrite must not undo it in either direction
    [TestCase(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead)]
    [TestCase(UnixFileMode.UserRead)]
    public async Task WithoutAModeAnExistingFileKeepsItsOwn(UnixFileMode existing)
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("unix permissions");
        await File.WriteAllTextAsync(_path, "{\"first\":true}");
        UnixMode.Set(_path, existing);

        await PrivateFile.Write(_path, "{\"second\":true}", null, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await File.ReadAllTextAsync(_path), Does.Contain("second"), "the replacement never landed");
            Assert.That(UnixMode.Of(_path), Is.EqualTo(existing));
        }
    }
}
