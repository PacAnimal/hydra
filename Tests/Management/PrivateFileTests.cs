using Hydra.Management;

namespace Tests.Management;

/// <summary>
/// The config directory's files are never readable by anyone else — not during the write, and not if the
/// machine dies in the middle of one.
/// </summary>
[TestFixture]
public class PrivateFileTests
{
    private string _directory = null!;
    private string _path = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "private-file", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, ".hydra-management.json");
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    // The platform guard lives here rather than at each call site: every test below is skipped on Windows
    // anyway, but the analyzer cannot see that through Assert.Ignore.
    private static UnixFileMode ModeOf(string path) => OperatingSystem.IsWindows() ? default : File.GetUnixFileMode(path);

    [Test]
    public async Task TheFileItWritesIsPrivate()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("unix permissions");

        await PrivateFile.Write(_path, "{\"secret\":\"value\"}", Private, CancellationToken.None);

        // The destination's mode comes from the TEMP, via the rename — nothing chmods it afterwards. So
        // this failing means the write ran through a temp at the umask default, which is a complete copy of
        // the secrets readable by anyone for the length of the write and after any crash.
        Assert.That(ModeOf(_path), Is.EqualTo(Private),
            "the file was written through a temp this type did not prepare, so it was world-readable while it was being written");
    }

    /// <summary>
    /// The temp file we prepared is the one the write used.
    ///
    /// <para><b>This is the test the permissions depend on.</b> A rename takes the SOURCE file's mode, and
    /// Cathedral's <c>DiskFileHandler</c> creates its temp under the process umask — 0644 — which left a
    /// complete copy of every controller secret world-readable for the whole write AND after any crash,
    /// under a deterministic name in a directory that sits beside the binary. <c>PrivateFile</c> creates
    /// that temp itself first, at 0600, so <c>File.Create</c> truncates rather than replaces it and the
    /// mode carries through. If Cathedral ever renames its temp, ours is left behind untouched — which is
    /// exactly what this asserts, because the alternative is the permissions quietly reverting.</para>
    /// </summary>
    [Test]
    public async Task TheTempFileIsTheOneWePreparedAndItIsPrivate()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("unix permissions");

        await PrivateFile.Write(_path, "{\"secret\":\"value\"}", Private, CancellationToken.None);

        Assert.That(File.Exists(PrivateFile.TempPathFor(_path)), Is.False,
            "the temp file this type prepared at 0600 was not the one the write promoted — Cathedral is using a different name, so the write ran through a 0644 temp");
    }

}
