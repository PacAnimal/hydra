using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Hydra.Management;
using Hydra.Platform.Windows;
using Tests.Setup;

namespace Tests.Management;

/// <summary>
/// Windows has no mode, so a private file is one whose DACL names only the principals Hydra trusts with its
/// secrets — the writer, the console user, SYSTEM and Administrators — and inherits nothing from the
/// directory it sits in.
/// </summary>
[TestFixture]
[SupportedOSPlatform("windows")]
public class PrivateFileWindowsTests
{
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private string _path = null!;

    [OneTimeSetUp]
    public void RequireWindows()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Windows-only: DACLs");
    }

    [SetUp]
    public void SetUp()
    {
        _path = Path.Combine(TestPaths.FreshFixtureRoot(nameof(PrivateFileWindowsTests)), ".hydra-management.json");
    }

    [TestCase(PrivateFile.OwnerOnly)]
    [TestCase(UnixFileMode.UserRead)]
    public async Task ANewOwnerOnlyFileIsPrivate(UnixFileMode mode)
    {
        await PrivateFile.Write(_path, "{\"secret\":\"value\"}", mode, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await File.ReadAllTextAsync(_path), Does.Contain("secret"));
            AssertPrivate(_path);
        }
    }

    // hydra.conf recreated by a SYSTEM rollback must stay readable by whoever could read it through the directory
    [TestCase(null)]
    [TestCase(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead)]
    public async Task ANewFileWithoutAnOwnerOnlyModeInheritsTheDirectorysAcl(UnixFileMode? mode)
    {
        await PrivateFile.Write(_path, "{\"first\":true}", mode, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await File.ReadAllTextAsync(_path), Does.Contain("first"));
            Assert.That(new FileInfo(_path).GetAccessControl().AreAccessRulesProtected, Is.False, "the file was made private");
        }
    }

    // an admin's ACL on hydra.conf is theirs to make
    [Test]
    public async Task WithoutAModeAnExistingFileKeepsItsAcl()
    {
        await File.WriteAllTextAsync(_path, "{\"first\":true}");
        GrantUsersRead(_path);

        await PrivateFile.Write(_path, "{\"second\":true}", null, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await File.ReadAllTextAsync(_path), Does.Contain("second"));
            Assert.That(Rules(_path).Any(r => r.IdentityReference.Equals(Users)), Is.True, "the admin's grant was dropped");
        }
    }

    // a secret file written before files were made private is made private on its next write
    [Test]
    public async Task OwnerOnlyMakesAnExistingFilePrivate()
    {
        await File.WriteAllTextAsync(_path, "{\"first\":true}");
        GrantUsersRead(_path);

        await PrivateFile.Write(_path, "{\"second\":true}", PrivateFile.OwnerOnly, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await File.ReadAllTextAsync(_path), Does.Contain("second"));
            AssertPrivate(_path);
        }
    }

    /// <summary>
    /// The console user keeps access whoever writes. Without it, the service's next write locks out the
    /// unelevated <c>hydra pair</c> that created the file, and a session child running on the user's own
    /// token loses the sidecar a SYSTEM child wrote.
    /// </summary>
    [Test]
    public async Task ThePrivateAclGrantsTheConsoleUserAsWellAsTheWriter()
    {
        var consoleUser = new SecurityIdentifier(WellKnownSidType.BuiltinGuestsSid, null);

        await PrivateFile.Write(_path, "{\"secret\":\"value\"}", PrivateFile.OwnerOnly, () => consoleUser, CancellationToken.None);

        AssertPrivate(_path, consoleUser);
    }

    private static void AssertPrivate(string path) => AssertPrivate(path, Win32Session.ActiveConsoleUser());

    private static void AssertPrivate(string path, SecurityIdentifier? consoleUser)
    {
        using var writer = WindowsIdentity.GetCurrent();
        var required = new[] { writer.User!, System, Administrators };
        var trusted = consoleUser is null ? required : [.. required, consoleUser];
        var security = new FileInfo(path).GetAccessControl();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(security.AreAccessRulesProtected, Is.True, "the file inherits the directory's ACL");
            Assert.That(Granted(security), Is.EquivalentTo(trusted.Distinct()), "the DACL is not exactly the trusted principals");
        }
    }

    private static List<IdentityReference> Granted(FileSystemSecurity security) =>
        [.. security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Select(r => r.IdentityReference)];

    private static List<FileSystemAccessRule> Rules(string path) =>
        [.. new FileInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()];

    private static void GrantUsersRead(string path)
    {
        var info = new FileInfo(path);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.Read, AccessControlType.Allow));
        info.SetAccessControl(security);
    }
}
