using Hydra.Management;

namespace Tests.Management;

/// <summary>
/// The side files' names are on disk in every existing install, so renaming one orphans a pending remote
/// apply, a pairing or a lock.
/// </summary>
[TestFixture]
public class ConfigDirTests
{
    [Test]
    public void SideFilesSitBesideTheConfig()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hydra-config-dir");
        var dir = new ConfigDir(Path.Combine(directory, "sub", "..", "hydra.conf"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dir.DirectoryPath, Is.EqualTo(directory));
            Assert.That(dir.ConfigLock, Is.EqualTo(Path.Combine(directory, ".hydra-config.lock")));
            Assert.That(dir.ManagementLock, Is.EqualTo(Path.Combine(directory, ".hydra-management.lock")));
            Assert.That(dir.ManagementState, Is.EqualTo(Path.Combine(directory, ".hydra-management.json")));
            Assert.That(dir.RemoteApplyMarker, Is.EqualTo(Path.Combine(directory, ".hydra-remote-apply.json")));
            Assert.That(dir.RemoteApplyBackup, Is.EqualTo(Path.Combine(directory, ".hydra-remote-backup.conf")));
        }
    }

    [Test]
    public void ARelativeConfigPathResolvesAgainstTheWorkingDirectory()
    {
        var dir = new ConfigDir("hydra.conf");

        Assert.That(dir.DirectoryPath, Is.EqualTo(Directory.GetCurrentDirectory()));
    }
}
