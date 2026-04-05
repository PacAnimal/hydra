using Hydra.Config;
using Hydra.Screen;
using Microsoft.Extensions.Configuration;

namespace Tests.Config;

[TestFixture]
public class HydraConfigTests
{
    private static IConfiguration ConfigFor(string path) =>
        new ConfigurationBuilder().AddInMemoryCollection([new("CONFIG", path)]).Build();

    [Test]
    public void Load_ReturnsValidConfig()
    {
        var config = HydraConfig.Load(ConfigFor("test.conf"));
        Assert.That(config, Is.Not.Null);
        Assert.That(config.Hosts, Is.Not.Empty);
    }

    [Test]
    public void Load_HasMainHost()
    {
        var config = HydraConfig.Load(ConfigFor("test.conf"));
        Assert.That(config.Hosts.Any(s => s.Name == "main"), Is.True);
    }

    [Test]
    public void Load_MainHost_HasNeighbour()
    {
        var config = HydraConfig.Load(ConfigFor("test.conf"));
        var main = config.Hosts.First(s => s.Name == "main");
        Assert.That(main.Neighbours, Is.Not.Empty);
    }

    [Test]
    public void Load_Neighbour_HasDirection()
    {
        var config = HydraConfig.Load(ConfigFor("test.conf"));
        var main = config.Hosts.First(s => s.Name == "main");
        var neighbour = main.Neighbours.First();
        Assert.That(neighbour.Direction, Is.EqualTo(Direction.Right));
    }

    [Test]
    public void Load_Neighbour_RangeDefaultsToFullEdge()
    {
        var config = HydraConfig.Load(ConfigFor("test.conf"));
        var main = config.Hosts.First(s => s.Name == "main");
        var neighbour = main.Neighbours.First();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(neighbour.SourceStart, Is.Zero);
            Assert.That(neighbour.SourceEnd, Is.EqualTo(100));
            Assert.That(neighbour.DestStart, Is.Zero);
            Assert.That(neighbour.DestEnd, Is.EqualTo(100));
        }
    }

    [Test]
    public void Load_Neighbour_ScreenIdentifiersDefaultToNull()
    {
        var config = HydraConfig.Load(ConfigFor("test.conf"));
        var main = config.Hosts.First(s => s.Name == "main");
        var neighbour = main.Neighbours.First();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(neighbour.SourceScreen, Is.Null);
            Assert.That(neighbour.DestScreen, Is.Null);
        }
    }

    [Test]
    public void Load_ScreenDefinitions_Deserialized()
    {
        var config = HydraConfig.Load(ConfigFor("test.conf"));
        Assert.That(config.ScreenDefinitions, Has.Count.EqualTo(2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(config.ScreenDefinitions[0].Match, Is.EqualTo("DELL U2720Q"));
            Assert.That(config.ScreenDefinitions[0].Scale, Is.EqualTo(1.5m));
            Assert.That(config.ScreenDefinitions[1].Match, Is.EqualTo("Built-in Retina Display"));
            Assert.That(config.ScreenDefinitions[1].Scale, Is.EqualTo(1.0m));
        }
    }

    [Test]
    public void LocalHost_ReturnsMatchingHost()
    {
        var config = new HydraConfig
        {
            Mode = Mode.Master,
            Name = "main",
            Hosts =
            [
                new HostConfig { Name = "main", Neighbours = [] },
                new HostConfig { Name = "right", Neighbours = [] },
            ],
        };
        Assert.That(config.LocalHost?.Name, Is.EqualTo("main"));
    }

    [Test]
    public void RemoteHosts_ExcludesLocalHost()
    {
        var config = new HydraConfig
        {
            Mode = Mode.Master,
            Name = "main",
            Hosts =
            [
                new HostConfig { Name = "main", Neighbours = [] },
                new HostConfig { Name = "right", Neighbours = [] },
                new HostConfig { Name = "other", Neighbours = [] },
            ],
        };
        var remotes = config.RemoteHosts.Select(h => h.Name).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(remotes, Does.Not.Contain("main"));
            Assert.That(remotes, Contains.Item("right"));
            Assert.That(remotes, Contains.Item("other"));
        }
    }
}
