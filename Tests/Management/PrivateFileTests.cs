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

        // A CREATE, and that is the only shape that can catch this. Cathedral takes `createMode ?? the
        // destination's current mode`, so a REPLACE keeps the mode whether or not we pass one — every
        // replace-shaped test here passes with the mode withheld from the handler. This test, the first
        // write in AReadOnlyFileCanStillBeReplacedAndStaysReadOnly, and PairingCode_IsSingleUseAndStoredAsAHash
        // are the whole of the coverage for that mistake.
        //
        // Failing here means the mode never reached the handler, so the file was written through a temp at
        // the process umask: a complete copy of the secrets readable by anyone for the length of the write,
        // and after any crash.
        Assert.That(ModeOf(_path), Is.EqualTo(Private),
            "the mode never reached the handler, so the file was written through a temp at the process umask — a complete copy readable by anyone");
    }

    /// <summary>
    /// A file whose own mode has no write bit can still be REPLACED, and stays read-only.
    ///
    /// <para><b>0400 and 0440 are ordinary hardening for a config holding secrets — the hardening this type
    /// exists to encourage.</b> Replacing a file has never needed write permission ON the file; a rename
    /// needs it on the directory. So stamping the target's mode onto our own temp must not make that temp
    /// unwritable to us, which is exactly what the first cut did: File.Create on our own 0400 temp failed
    /// with permission denied and every write path broke.</para>
    ///
    /// <para>Worse than a failed save, and the reason this is a test rather than a note: RollbackAsync and
    /// the startup restore propagate the same mode, so on a hardened config the automatic rollback of an
    /// unconfirmed remote apply fails, retries every few seconds for ever, and leaves a distant machine on
    /// a candidate nobody confirmed — the precise outcome the remote-apply machinery exists to prevent.</para>
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
            Assert.That(ModeOf(_path), Is.EqualTo(readOnly), "the file came back writable");
        }
    }
}
