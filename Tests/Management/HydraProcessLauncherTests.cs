using Hydra.Config;
using Hydra.Platform;
using Hydra.Platform.MacOs;
using Tests.Setup;

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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(startInfo.FileName, Is.EqualTo(executablePath));
            Assert.That(startInfo.WorkingDirectory, Is.EqualTo(workingDirectory));
            Assert.That(startInfo.Environment[HydraArgs.ConfigVariable], Is.EqualTo(configPath));
            Assert.That(startInfo.RedirectStandardOutput, Is.True);
            Assert.That(startInfo.RedirectStandardError, Is.True);
        }
    }

    [Test]
    public void TheAgentStartsWhenItRunsTheRequestedConfig()
    {
        var configPath = Path.Combine(Path.GetTempPath(), "hydra", "hydra.conf");

        Assert.That(() => HydraProcessLauncher.EnsureAgentRuns(configPath, configPath), Throws.Nothing);
    }

    // starting it anyway would run another config and leave the TUI polling an endpoint nobody opens
    [Test]
    public void TheAgentIsRefusedWhenItRunsAnotherConfig()
    {
        var agentConfig = Path.Combine(Path.GetTempPath(), "installed", "hydra.conf");
        var requested = Path.Combine(Path.GetTempPath(), "other", "hydra.conf");

        Assert.That(() => HydraProcessLauncher.EnsureAgentRuns(agentConfig, requested), Throws.InvalidOperationException
            .With.Message.Contains(agentConfig).And.Message.Contains(requested));
    }

    // /tmp is /private/tmp on macOS, and a config reached through either is the agent's
    [Test]
    public void TheAgentStartsForItsConfigReachedThroughALinkedDirectory()
    {
        var root = TestPaths.FreshFixtureRoot(nameof(HydraProcessLauncherTests));
        var real = Directory.CreateDirectory(Path.Combine(root, "real")).FullName;
        var linked = Path.Combine(root, "linked");
        Directory.CreateSymbolicLink(linked, real);
        File.WriteAllText(Path.Combine(real, "hydra.conf"), "{}");

        Assert.That(() => HydraProcessLauncher.EnsureAgentRuns(Path.Combine(real, "hydra.conf"), Path.Combine(linked, "hydra.conf")), Throws.Nothing);
    }

    // the same file in another case, where the filesystem folds case, and a different one where it does not
    [Test]
    public void TheAgentsConfigInAnotherCaseIsTheSameFileOnlyWhereTheFilesystemSaysSo()
    {
        var root = TestPaths.FreshFixtureRoot(nameof(HydraProcessLauncherTests));
        var agentConfig = Path.Combine(root, "hydra.conf");
        File.WriteAllText(agentConfig, "{}");
        var requested = Path.Combine(root, "HYDRA.CONF");

        if (File.Exists(requested))
            Assert.That(() => HydraProcessLauncher.EnsureAgentRuns(agentConfig, requested), Throws.Nothing);
        else
            Assert.That(() => HydraProcessLauncher.EnsureAgentRuns(agentConfig, requested), Throws.InvalidOperationException);
    }

    // the management endpoint is named by the spelling, so the TUI must use the one the agent was installed with
    [Test]
    public void TheTuiAddressesTheAgentByTheAgentsSpellingOfTheSameConfig()
    {
        var root = TestPaths.FreshFixtureRoot(nameof(HydraProcessLauncherTests));
        var real = Directory.CreateDirectory(Path.Combine(root, "real")).FullName;
        var linked = Path.Combine(root, "linked");
        Directory.CreateSymbolicLink(linked, real);
        var agentConfig = Path.Combine(real, "hydra.conf");
        File.WriteAllText(agentConfig, "{}");
        var other = Path.Combine(root, "other.conf");
        File.WriteAllText(other, "{}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(HydraProcessLauncher.AddressedAs(Path.Combine(linked, "hydra.conf"), agentConfig), Is.EqualTo(agentConfig));
            Assert.That(HydraProcessLauncher.AddressedAs(other, agentConfig), Is.EqualTo(other));
            Assert.That(HydraProcessLauncher.AddressedAs(other, null), Is.EqualTo(other));
        }
    }

    // the TUI starts before Start's EnsureAgentRuns gets to report a broken plist, so it must not throw here
    [Test]
    public void AnUnreadableAgentPlistAddressesTheConfigAsGiven()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(HydraProcessLauncher.AgentConfigPathOrNull(() => AgentPlist.ConfigPathOf("not a plist")), Is.Null);
            Assert.That(HydraProcessLauncher.AgentConfigPathOrNull(() => AgentPlist.ConfigPathOf("<plist/>")), Is.Null);
            Assert.That(HydraProcessLauncher.AgentConfigPathOrNull(() => throw new UnauthorizedAccessException()), Is.Null);
            Assert.That(HydraProcessLauncher.AgentConfigPathOrNull(() => "/agent/hydra.conf"), Is.EqualTo("/agent/hydra.conf"));
        }
    }
}
