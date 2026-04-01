using Hydra.Config;
using Hydra.Screen;

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
    public void Load_MainScreen_HasNeighbour()
    {
        var config = HydraConfig.Load();
        var main = config.Screens.First(s => s.Name == "main");
        Assert.That(main.Neighbours, Is.Not.Empty);
    }

    [Test]
    public void Load_Neighbour_HasDirection()
    {
        var config = HydraConfig.Load();
        var main = config.Screens.First(s => s.Name == "main");
        var neighbour = main.Neighbours.First();
        Assert.That(neighbour.Direction, Is.EqualTo(Direction.Right));
    }

    [Test]
    public void Load_Neighbour_ScaleDefaultsToOne()
    {
        var config = HydraConfig.Load();
        var main = config.Screens.First(s => s.Name == "main");
        var neighbour = main.Neighbours.First();
        Assert.That(neighbour.Scale, Is.EqualTo(1.0m));
    }

    [Test]
    public void Load_Neighbour_OffsetDefaultsToZero()
    {
        var config = HydraConfig.Load();
        var main = config.Screens.First(s => s.Name == "main");
        var neighbour = main.Neighbours.First();
        Assert.That(neighbour.Offset, Is.Zero);
    }
}
