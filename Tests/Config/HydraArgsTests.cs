using Hydra.Config;
using Hydra.Platform;

namespace Tests.Config;

[TestFixture]
public class HydraArgsTests
{
    [Test]
    public void ConfigOption_TakesTheNextArgument()
    {
        var parsed = HydraArgs.Parse(["--config", "/etc/hydra.conf"], null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed.ConfigPath, Is.EqualTo("/etc/hydra.conf"));
            Assert.That(parsed.Rest, Is.Empty);
        }
    }

    [Test]
    public void ConfigOption_AcceptsTheEqualsForm()
    {
        var parsed = HydraArgs.Parse(["--config=/etc/hydra.conf"], null);

        Assert.That(parsed.ConfigPath, Is.EqualTo("/etc/hydra.conf"));
    }

    [Test]
    public void ConfigOption_BeatsTheEnvironment()
    {
        var parsed = HydraArgs.Parse(["--config", "/etc/hydra.conf"], "/env/hydra.conf");

        Assert.That(parsed.ConfigPath, Is.EqualTo("/etc/hydra.conf"));
    }

    [Test]
    public void Environment_IsTheFallback()
    {
        var parsed = HydraArgs.Parse(["--session"], "/env/hydra.conf");

        Assert.That(parsed.ConfigPath, Is.EqualTo("/env/hydra.conf"));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void NeitherOptionNorEnvironment_LeavesThePathUnset(string? environment)
    {
        var parsed = HydraArgs.Parse([], environment);

        Assert.That(parsed.ConfigPath, Is.Null);
    }

    [Test]
    public void OtherArguments_AreKeptInOrder()
    {
        var parsed = HydraArgs.Parse(["--demo", "--config", "a.conf", "--session"], null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed.ConfigPath, Is.EqualTo("a.conf"));
            Assert.That(parsed.Rest, Is.EqualTo(["--demo", "--session"]));
        }
    }

    [TestCase("--config")]
    [TestCase("--config=")]
    [TestCase("--config", "")]
    [TestCase("--config", "--demo")]
    public void ConfigOption_WithoutAPath_IsRefused(params string[] args)
    {
        Assert.That(() => HydraArgs.Parse(args, "/env/hydra.conf"),
            Throws.ArgumentException.With.Message.Contains("--config"));
    }

    // two configs is a typo or a script appending to a command line, and neither one is obviously meant
    [TestCase("--config", "a.conf", "--config", "b.conf")]
    [TestCase("--config=a.conf", "--config", "a.conf")]
    public void ConfigOption_GivenTwice_IsRefused(params string[] args) =>
        Assert.That(() => HydraArgs.Parse(args, null), Throws.ArgumentException.With.Message.Contains("once"));

    // CONFIG is how the direct launcher talks to its child, not a choice to bake into a service
    [Test]
    public void InstallConfigPath_IgnoresTheEnvironment() =>
        Assert.That(HydraArgs.Parse(["--install"], "/env/hydra.conf").InstallConfigPath, Is.Null);

    [Test]
    public void InstallConfigPath_IsTheExplicitConfigMadeAbsolute() =>
        Assert.That(HydraArgs.Parse(["--install", "--config", "x.conf"], "/env/hydra.conf").InstallConfigPath,
            Is.EqualTo(Path.GetFullPath("x.conf")));

    // the direct launcher still hands the path over through the environment
    [Test]
    public void TheLaunchersEnvironment_ResolvesToItsConfig()
    {
        var configPath = Path.Combine(Path.GetTempPath(), "hydra-args-tests", "hydra.conf");
        var startInfo = HydraProcessLauncher.CreateDirectStartInfo(Path.Combine(Path.GetTempPath(), "Hydra"), configPath);

        var parsed = HydraArgs.Parse([], startInfo.Environment[HydraArgs.ConfigVariable]);

        Assert.That(parsed.ConfigPath, Is.EqualTo(configPath));
    }

    [Test]
    public void SessionChildArguments_ForwardTheConfigQuoted() =>
        Assert.That(HydraArgs.SessionChildArguments(@"C:\Program Files\Hydra\hydra.conf"),
            Is.EqualTo("--session --config \"C:\\Program Files\\Hydra\\hydra.conf\""));

    [TestCase("plain", "\"plain\"")]
    [TestCase(@"C:\dir\", @"""C:\dir\\""")]
    [TestCase(@"a\""b", @"""a\\\""b""")]
    [TestCase(@"a\\b", @"""a\\b""")]
    [TestCase("", "\"\"")]
    public void QuoteWindowsArgument_FollowsCommandLineToArgvW(string arg, string expected) =>
        Assert.That(HydraArgs.QuoteWindowsArgument(arg), Is.EqualTo(expected));

    [TestCase("--CONFIG", "/etc/hydra.conf")]
    [TestCase("--Config=/etc/hydra.conf")]
    public void ConfigOption_IgnoresCase(params string[] args) =>
        Assert.That(HydraArgs.Parse(args, null).ConfigPath, Is.EqualTo("/etc/hydra.conf"));

    [TestCase("tui", "--config", "x.conf", "--demo")]
    [TestCase("--config", "x.conf", "tui", "--demo")]
    [TestCase("--config=x.conf", "TUI", "--demo")]
    public void SplitCommand_FindsTheCommandEitherSideOfTheConfig(params string[] args)
    {
        var split = HydraArgs.SplitCommand(args);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(split.Command, Is.EqualTo(HydraArgs.TuiCommand).IgnoreCase);
            Assert.That(HydraArgs.Parse(split.Arguments, null).ConfigPath, Is.EqualTo("x.conf"));
            Assert.That(HydraArgs.Parse(split.Arguments, null).Rest, Is.EqualTo(["--demo"]));
        }
    }

    // a command may follow only the config option; after anything else, or as the config's path, it is the daemon's to refuse
    [TestCase("--install")]
    [TestCase("--demo", "tui")]
    [TestCase("--config", "pair")]
    public void SplitCommand_LeavesTheDaemonsArgumentsAlone(params string[] args)
    {
        var split = HydraArgs.SplitCommand(args);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(split.Command, Is.Null);
            Assert.That(split.Arguments, Is.EqualTo(args));
        }
    }

    [Test]
    public void UnknownArgument_NamesTheFirstOneNotAllowed()
    {
        var parsed = HydraArgs.Parse(["--INSTALL", "--conifg", "x"], null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed.UnknownArgument(HydraArgs.InstallOption), Is.EqualTo("--conifg"));
            Assert.That(parsed.Has(HydraArgs.InstallOption), Is.True);
        }
    }

    [Test]
    public void UnknownArgument_IsNullWhenEveryArgumentIsAllowed() =>
        Assert.That(HydraArgs.Parse(["--config", "x", "--service"], null).UnknownArgument(HydraArgs.ServiceOption, HydraArgs.SessionOption), Is.Null);

    [Test]
    public void ServiceCommandLine_CarriesTheConfigQuoted() =>
        Assert.That(HydraArgs.ServiceCommandLine(@"C:\Program Files\Hydra\Hydra.exe", @"C:\Hydra Config\hydra.conf"),
            Is.EqualTo("\"C:\\Program Files\\Hydra\\Hydra.exe\" --service --config \"C:\\Hydra Config\\hydra.conf\""));

    // no --config at install: the service searches beside its binary, as it always has
    [Test]
    public void ServiceCommandLine_WithoutAConfig_IsTheBareService() =>
        Assert.That(HydraArgs.ServiceCommandLine(@"C:\Hydra\Hydra.exe", null), Is.EqualTo("\"C:\\Hydra\\Hydra.exe\" --service"));
}
