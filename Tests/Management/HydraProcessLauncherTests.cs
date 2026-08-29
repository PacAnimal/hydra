using Hydra.Platform;

namespace Tests.Management;

public class HydraProcessLauncherTests
{
    [Test]
    public void DirectStartInfoPassesTheConfigPathToTheChild()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "hydra-launcher-tests");
        var executablePath = Path.Combine(workingDirectory, "Hydra");
        var configPath = Path.Combine(workingDirectory, "hydra.conf");

        var startInfo = HydraProcessLauncher.CreateDirectStartInfo(executablePath, configPath);

        Assert.Multiple(() =>
        {
            Assert.That(startInfo.FileName, Is.EqualTo(executablePath));
            Assert.That(startInfo.WorkingDirectory, Is.EqualTo(workingDirectory));
            Assert.That(startInfo.Environment["CONFIG"], Is.EqualTo(configPath));
            Assert.That(startInfo.RedirectStandardOutput, Is.True);
            Assert.That(startInfo.RedirectStandardError, Is.True);
        });
    }
}
