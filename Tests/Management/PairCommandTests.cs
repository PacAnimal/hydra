using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Hydra.Management;
using Tests.Setup;

namespace Tests.Management;

public class PairCommandTests
{
    private string _configPath = null!;
    private string _statePath = null!;
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();

    [SetUp]
    public void SetUp()
    {
        _configPath = Path.Combine(TestPaths.FreshFixtureRoot(nameof(PairCommandTests)), "hydra.conf");
        _statePath = new ConfigDir(_configPath).ManagementState;
        _output.GetStringBuilder().Clear();
        _error.GetStringBuilder().Clear();
    }

    [TearDown]
    public void GiveTheStateBack()
    {
        if (!File.Exists(_statePath)) return;
        if (OperatingSystem.IsWindows()) SetOnlyGrant(_statePath, CurrentUser());
        else UnixMode.Set(_statePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [OneTimeTearDown]
    public void DisposeWriters()
    {
        _output.Dispose();
        _error.Dispose();
    }

    [Test]
    public async Task PrintsAOneTimeCode()
    {
        var exitCode = await PairCommand.Run(["--config", _configPath], _output, _error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.Zero);
            Assert.That(_output.ToString(), Does.Contain("one-time code"));
            Assert.That(File.Exists(_statePath), Is.True);
        }
    }

    [TestCase("--config")]
    [TestCase("--config", "x.conf", "--demo")]
    public async Task ABadCommandLineExitsWithTheUsageCode(params string[] args)
    {
        var exitCode = await PairCommand.Run(args, _output, _error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.EqualTo(2));
            Assert.That(_error.ToString(), Does.Contain(Hydra.Config.HydraArgs.PairUsage));
        }
    }

    [Test]
    public async Task ADoubledConfigSaysWhy()
    {
        var exitCode = await PairCommand.Run(["--config", _configPath, "--config", _configPath], _output, _error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.EqualTo(2));
            Assert.That(_error.ToString(), Does.Contain("may be given only once"));
            Assert.That(_error.ToString(), Does.Contain(Hydra.Config.HydraArgs.PairUsage));
        }
    }

    // a crash mid-write leaves the temp behind, and one this account cannot remove is the same denial
    [Test]
    public async Task AStaleTempItCannotReplaceSaysHowToRunIt()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Windows-only: a directory in the way of Cathedral's temp is an access denial only there");
        Directory.CreateDirectory(_statePath + ".tmp");

        var exitCode = await PairCommand.Run(["--config", _configPath], _output, _error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(_error.ToString(), Does.Contain("elevated"));
            Assert.That(_error.ToString(), Does.Not.Contain("   at "), "a stack trace reached the user");
        }
    }

    // a temp another process still holds open is a message about what is in the way and not a crash
    [Test]
    public async Task AStaleTempHeldOpenSaysWhatIsInTheWay()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Windows-only: an open file cannot be deleted");
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        await using var held = new FileStream(_statePath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None);

        var exitCode = await PairCommand.Run(["--config", _configPath], _output, _error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(_error.ToString(), Does.Contain(_statePath + ".tmp"));
            Assert.That(_error.ToString(), Does.Contain("another process"));
            Assert.That(_error.ToString(), Does.Not.Contain("   at "), "a stack trace reached the user");
        }
    }

    // the service's sidecar, denied to whoever ran pair, is a message about where to run it and not a crash
    [Test]
    public async Task StateItMayNotReadSaysHowToRunIt()
    {
        if (!OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess) Assert.Ignore("root reads anything");
        await File.WriteAllTextAsync(_statePath, "{}");
        if (OperatingSystem.IsWindows()) SetOnlyGrant(_statePath, new SecurityIdentifier(WellKnownSidType.BuiltinGuestsSid, null));
        else UnixMode.Set(_statePath, UnixFileMode.None);

        var exitCode = await PairCommand.Run(["--config", _configPath], _output, _error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(_error.ToString(), Does.Contain(_statePath));
            Assert.That(_error.ToString(), Does.Contain(OperatingSystem.IsWindows() ? "elevated" : "sudo"));
            Assert.That(_error.ToString(), Does.Not.Contain("   at "), "a stack trace reached the user");
        }
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!;
    }

    // the owner keeps the right to change the DACL whatever it says, which is how TearDown gets the file back
    [SupportedOSPlatform("windows")]
    private static void SetOnlyGrant(string path, SecurityIdentifier principal)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(principal, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }
}
