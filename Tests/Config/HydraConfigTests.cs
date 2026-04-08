using Hydra.Config;
using Hydra.Screen;
using Microsoft.Extensions.Configuration;

namespace Tests.Config;

[TestFixture]
public class HydraConfigTests
{
    private static IConfiguration ConfigFor(string path) =>
        new ConfigurationBuilder().AddInMemoryCollection([new("CONFIG", path)]).Build();

    private static HydraConfig MakeConfig(Mode mode = Mode.Master, ConfigConditions? conditions = null) =>
        new() { Mode = mode, Conditions = conditions };

    private static ConfigConditions SsidCondition(string ssid) => new() { Ssid = ssid };
    private static ConfigConditions ScreenCountCondition(int count) => new() { ScreenCount = count };
    private static ConfigConditions SsidAndScreenCount(string ssid, int count) => new() { Ssid = ssid, ScreenCount = count };

    private static ConditionState State(List<string>? ssids = null, int screenCount = 1) =>
        new(ssids ?? [], screenCount);

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
            Assert.That(config.ScreenDefinitions[0].DisplayName, Is.EqualTo("DELL U2720Q"));
            Assert.That(config.ScreenDefinitions[0].MouseScale, Is.EqualTo(1.5m));
            Assert.That(config.ScreenDefinitions[1].DisplayName, Is.EqualTo("Built-in Retina Display"));
            Assert.That(config.ScreenDefinitions[1].MouseScale, Is.EqualTo(1.0m));
        }
    }

    [Test]
    public void Load_RootMouseScale_Deserialized()
    {
        var json = """{ "mode": "Slave", "mouseScale": 2.5 }""";
        var configs = HydraConfig.ParseAndValidate(json);
        Assert.That(configs[0].MouseScale, Is.EqualTo(2.5m));
    }

    [Test]
    public void ScreenDefinition_Validation_ThrowsWhenNoMatchCriteria()
    {
        var json = """{ "mode": "Master", "screenDefinitions": [{ "mouseScale": 1.5 }] }""";
        Assert.That(() => HydraConfig.ParseAndValidate(json), Throws.InvalidOperationException
            .With.Message.Contains("no matching criteria"));
    }

    [Test]
    public void Load_ThrowsFileNotFound_WhenNoConfigFound()
    {
        var config = new ConfigurationBuilder().Build(); // no CONFIG set, no hydra.conf on disk
        Assert.That(() => HydraConfig.Load(config), Throws.InstanceOf<FileNotFoundException>()
            .With.Message.Contains("CONFIG="));
    }

    // HasConditions

    [Test]
    public void HasConditions_ReturnsFalse_WhenSingleUnconditionalConfig()
    {
        var configs = new List<HydraConfig> { MakeConfig() };
        Assert.That(HydraConfig.HasConditions(configs), Is.False);
    }

    [Test]
    public void HasConditions_ReturnsTrue_WhenAnyConditionalConfig()
    {
        var configs = new List<HydraConfig> { MakeConfig(), MakeConfig(conditions: SsidCondition("HomeNet")) };
        Assert.That(HydraConfig.HasConditions(configs), Is.True);
    }

    // Resolve

    [Test]
    public void Resolve_SingleUnconditional_ReturnsItRegardlessOfNetwork()
    {
        var cfg = MakeConfig();
        var configs = new List<HydraConfig> { cfg };
        // no SSIDs at all — should still pick the unconditional config
        Assert.That(HydraConfig.Resolve(configs, State()), Is.SameAs(cfg));
    }

    [Test]
    public void Resolve_SsidCondition_MatchesCorrectSsid()
    {
        var home = MakeConfig(conditions: SsidCondition("HomeNet"));
        var fallback = MakeConfig(Mode.Slave);
        var configs = new List<HydraConfig> { home, fallback };
        Assert.That(HydraConfig.Resolve(configs, State(["HomeNet"])), Is.SameAs(home));
    }

    [Test]
    public void Resolve_SsidCondition_IsCaseInsensitive()
    {
        var home = MakeConfig(conditions: SsidCondition("HomeNet"));
        var configs = new List<HydraConfig> { home };
        Assert.That(HydraConfig.Resolve(configs, State(["homenet"])), Is.SameAs(home));
    }

    [Test]
    public void Resolve_SsidCondition_DoesNotMatchDifferentSsid()
    {
        var home = MakeConfig(conditions: SsidCondition("HomeNet"));
        var configs = new List<HydraConfig> { home };
        Assert.That(HydraConfig.Resolve(configs, State(["OfficeNet"])), Is.Null);
    }

    [Test]
    public void Resolve_FallsBackToUnconditional_WhenNoConditionMatches()
    {
        var ssid = MakeConfig(conditions: SsidCondition("HomeNet"));
        var fallback = MakeConfig(Mode.Slave);
        var configs = new List<HydraConfig> { ssid, fallback };
        Assert.That(HydraConfig.Resolve(configs, State(["OfficeNet"])), Is.SameAs(fallback));
    }

    [Test]
    public void Resolve_ReturnsNull_WhenNoMatchAndNoFallback()
    {
        var ssid = MakeConfig(conditions: SsidCondition("HomeNet"));
        var configs = new List<HydraConfig> { ssid };
        Assert.That(HydraConfig.Resolve(configs, State()), Is.Null);
    }

    [Test]
    public void Resolve_ScreenCount_MatchesWhenEqual()
    {
        var cfg = MakeConfig(conditions: ScreenCountCondition(2));
        var configs = new List<HydraConfig> { cfg };
        Assert.That(HydraConfig.Resolve(configs, State(screenCount: 2)), Is.SameAs(cfg));
    }

    [Test]
    public void Resolve_ScreenCount_DoesNotMatchWhenDifferent()
    {
        var cfg = MakeConfig(conditions: ScreenCountCondition(2));
        var configs = new List<HydraConfig> { cfg };
        Assert.That(HydraConfig.Resolve(configs, State(screenCount: 1)), Is.Null);
    }

    [Test]
    public void Resolve_SsidAndScreenCount_BothMustMatch()
    {
        var office2 = MakeConfig(conditions: SsidAndScreenCount("Office", 2));
        var fallback = MakeConfig(Mode.Slave);
        var configs = new List<HydraConfig> { office2, fallback };
        Assert.That(HydraConfig.Resolve(configs, State(["Office"], screenCount: 2)), Is.SameAs(office2));
    }

    [Test]
    public void Resolve_SsidAndScreenCount_SsidMatchButCountMismatch_FallsThrough()
    {
        var office2 = MakeConfig(conditions: SsidAndScreenCount("Office", 2));
        var fallback = MakeConfig(Mode.Slave);
        var configs = new List<HydraConfig> { office2, fallback };
        Assert.That(HydraConfig.Resolve(configs, State(["Office"], screenCount: 1)), Is.SameAs(fallback));
    }

    [Test]
    public void Resolve_SsidAndScreenCount_CountMatchButSsidMismatch_FallsThrough()
    {
        var office2 = MakeConfig(conditions: SsidAndScreenCount("Office", 2));
        var fallback = MakeConfig(Mode.Slave);
        var configs = new List<HydraConfig> { office2, fallback };
        Assert.That(HydraConfig.Resolve(configs, State(["Home"], screenCount: 2)), Is.SameAs(fallback));
    }

    // Validate

    [Test]
    public void Validate_Throws_OnMultipleDefaults()
    {
        var json = """[{"mode":"Master"},{"mode":"Slave"}]""";
        Assert.That(() => HydraConfig.ParseAndValidate(json),
            Throws.InvalidOperationException.With.Message.Contains("multiple default"));
    }

    [Test]
    public void Validate_Throws_OnDuplicateSsid()
    {
        var json = """
            [
              {"mode":"Master","conditions":{"ssid":"Home"}},
              {"mode":"Slave","conditions":{"ssid":"Home"}}
            ]
            """;
        Assert.That(() => HydraConfig.ParseAndValidate(json),
            Throws.InvalidOperationException.With.Message.Contains("duplicate conditions"));
    }

    [Test]
    public void Validate_EmptyConditions_TreatedAsUnconditional()
    {
        // {} is the same as no conditions — valid, treated as fallback
        var json = """[{"mode":"Master","conditions":{}},{"mode":"Slave","conditions":{"ssid":"Home"}}]""";
        Assert.That(() => HydraConfig.ParseAndValidate(json), Throws.Nothing);
    }

    [Test]
    public void Validate_Throws_OnDuplicateConditionTuple()
    {
        var json = """
            [
              {"mode":"Master","conditions":{"ssid":"Home","screenCount":2}},
              {"mode":"Slave","conditions":{"ssid":"Home","screenCount":2}}
            ]
            """;
        Assert.That(() => HydraConfig.ParseAndValidate(json),
            Throws.InvalidOperationException.With.Message.Contains("duplicate conditions"));
    }

    [Test]
    public void Validate_SameSSID_DifferentScreenCount_IsAllowed()
    {
        var json = """
            [
              {"mode":"Master","conditions":{"ssid":"Office","screenCount":1}},
              {"mode":"Master","conditions":{"ssid":"Office","screenCount":2}},
              {"mode":"Slave"}
            ]
            """;
        Assert.That(() => HydraConfig.ParseAndValidate(json), Throws.Nothing);
    }

    [Test]
    public void Validate_Throws_WhenScreenCountIsZero()
    {
        var json = """[{"mode":"Master","conditions":{"screenCount":0}}]""";
        Assert.That(() => HydraConfig.ParseAndValidate(json),
            Throws.InvalidOperationException.With.Message.Contains("screenCount"));
    }

    [Test]
    public void Validate_Accepts_ScreenCountOnlyCondition()
    {
        var json = """
            [
              {"mode":"Master","conditions":{"screenCount":2}},
              {"mode":"Slave"}
            ]
            """;
        Assert.That(() => HydraConfig.ParseAndValidate(json), Throws.Nothing);
    }

    [Test]
    public void Validate_Accepts_MultipleConditionalConfigs()
    {
        var json = """
            [
              {"mode":"Master","conditions":{"ssid":"Office"}},
              {"mode":"Master","conditions":{"ssid":"Home"}},
              {"mode":"Slave"}
            ]
            """;
        Assert.That(() => HydraConfig.ParseAndValidate(json), Throws.Nothing);
    }

    // LocalHost / RemoteHosts

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

    // RemoteOnly validation

    [Test]
    public void RemoteOnly_RequiresMasterMode()
    {
        var json = """{ "mode": "Slave", "remoteOnly": true, "hosts": [{ "name": "mac" }] }""";
        Assert.That(() => HydraConfig.ParseAndValidate(json),
            Throws.InvalidOperationException.With.Message.Contains("mode: Master"));
    }

    [Test]
    public void RemoteOnly_RequiresAtLeastOneRemoteHost()
    {
        var json = """{ "mode": "Master", "remoteOnly": true, "hosts": [] }""";
        Assert.That(() => HydraConfig.ParseAndValidate(json),
            Throws.InvalidOperationException.With.Message.Contains("remote host"));
    }

    [Test]
    public void RemoteOnly_ValidConfig_Parses()
    {
        var json = """{ "mode": "Master", "remoteOnly": true, "hosts": [{ "name": "mac" }] }""";
        Assert.That(() => HydraConfig.ParseAndValidate(json), Throws.Nothing);
    }
}
