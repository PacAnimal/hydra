using Hydra.Config;

namespace Tests.Config;

[TestFixture]
public class HydraConfigTests
{
    [Test]
    public void Load_ReturnsValidConfig()
    {
        var config = HydraConfig.Load();
        Assert.That(config, Is.Not.Null);
        Assert.That(config.Screens, Is.Not.Empty);
    }

    [Test]
    public void Load_HasMainScreen()
    {
        var config = HydraConfig.Load();
        Assert.That(config.Screens.Any(s => s.Name == "main"), Is.True);
    }

    [Test]
    public void Load_HasVirtualScreen()
    {
        var config = HydraConfig.Load();
        Assert.That(config.Screens.Any(s => s.IsVirtual), Is.True);
    }

    [Test]
    public void Load_MainScreenNotVirtual()
    {
        var config = HydraConfig.Load();
        var main = config.Screens.First(s => s.Name == "main");
        Assert.That(main.IsVirtual, Is.False);
    }
}
