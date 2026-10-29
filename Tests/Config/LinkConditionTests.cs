using System.Net;
using System.Net.NetworkInformation;
using Hydra.Config;
using Hydra.Platform;
using static Tests.Setup.ConfigJson;

namespace Tests.Config;

[TestFixture]
public class LinkConditionTests
{
    private const string Relay = ",\"embeddedStyx\":{\"server\":\"https://localhost:5000\",\"password\":\"test\"}";
    private static readonly PhysicalAddress OfficeRouter = PhysicalAddress.Parse("A4-91-B1-0C-22-7E");

    private static NetworkLinks Links(bool wired = false, PhysicalAddress? gateway = null, params string[] addresses) =>
        new([.. addresses.Select(IPAddress.Parse)], gateway == null ? [] : [gateway], wired);

    private static bool Matches(ConfigConditions conditions, NetworkLinks? links) =>
        conditions.Matches(new ConditionState([], 1, null, links));

    [TestCase("192.168.1.7", "192.168.1.7", true)]
    [TestCase("192.168.1.7", "192.168.1.8", false)]
    [TestCase("192.168.1.0/24", "192.168.1.200", true)]
    [TestCase("192.168.1.0/24", "192.168.2.1", false)]
    [TestCase("10.20.0.0/16", "10.20.255.1", true)]
    [TestCase("2001:db8::/32", "2001:db8:1::5", true)]
    [TestCase("2001:db8::/32", "192.168.1.7", false)]
    public void Address_MatchesAnAddressOfOursInTheNetwork(string condition, string ours, bool matches)
    {
        Assert.That(Matches(new ConfigConditions { Address = condition }, Links(addresses: ["fe80::1", ours])), Is.EqualTo(matches));
    }

    [TestCase("a4:91:b1:0c:22:7e")]
    [TestCase("A4-91-B1-0C-22-7E")]
    [TestCase("a4:91:b1:c:22:7e")]
    [TestCase("a491b10c227e")]
    public void GatewayMac_MatchesInAnySpelling(string condition)
    {
        Assert.That(Matches(new ConfigConditions { GatewayMac = condition }, Links(gateway: OfficeRouter)), Is.True);
    }

    // a gateway not yet resolved is unknown, never another router or no router
    [Test]
    public void GatewayMac_DoesNotMatchWhileUnresolved()
    {
        Assert.That(Matches(new ConfigConditions { GatewayMac = "a4:91:b1:0c:22:7e" }, new NetworkLinks([], null, false)), Is.False);
    }

    [Test]
    public void GatewayMac_DoesNotMatchAnotherRouter()
    {
        Assert.That(Matches(new ConfigConditions { GatewayMac = "a4:91:b1:0c:22:7f" }, Links(gateway: OfficeRouter)), Is.False);
    }

    [TestCase(true, true, true)]
    [TestCase(true, false, false)]
    [TestCase(false, false, true)]
    [TestCase(false, true, false)]
    public void Wired_MatchesTheLink(bool condition, bool wired, bool matches)
    {
        Assert.That(Matches(new ConfigConditions { Wired = condition }, Links(wired)), Is.EqualTo(matches));
    }

    // unknown is never "not wired" or "no gateway"
    [Test]
    public void UnknownLinks_MatchNoLinkCondition()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Matches(new ConfigConditions { Wired = false }, null), Is.False);
            Assert.That(Matches(new ConfigConditions { Address = "0.0.0.0/0" }, null), Is.False);
            Assert.That(Matches(new ConfigConditions { GatewayMac = "a4:91:b1:0c:22:7e" }, null), Is.False);
        }
    }

    [Test]
    public void LinkConditions_AndWithTheOthers()
    {
        var docked = new ConfigConditions { Wired = true, GatewayMac = "a4:91:b1:0c:22:7e", IsPluggedIn = true };
        var links = Links(true, OfficeRouter, "10.1.2.3");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(docked.Matches(new ConditionState([], 1, true, links)), Is.True);
            Assert.That(docked.Matches(new ConditionState([], 1, false, links)), Is.False);
        }
    }

    [Test]
    public void Resolve_PicksTheWiredProfile_AndFallsBackOffTheCable()
    {
        var docked = new HydraConfig { Mode = Mode.Master, Conditions = new ConfigConditions { Wired = true } };
        var fallback = new HydraConfig { Mode = Mode.Slave };
        List<HydraConfig> profiles = [docked, fallback];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(HydraConfig.Resolve(profiles, new ConditionState([], 1, null, Links(true))), Is.SameAs(docked));
            Assert.That(HydraConfig.Resolve(profiles, new ConditionState([], 1, null, Links())), Is.SameAs(fallback));
        }
    }

    [Test]
    public void HasLinkConditions_SeesEachOfThem()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(HydraConfig.HasLinkConditions([new HydraConfig { Mode = Mode.Master, Conditions = new ConfigConditions { Ssid = "x" } }]), Is.False);
            Assert.That(HydraConfig.HasLinkConditions([new HydraConfig { Mode = Mode.Master, Conditions = new ConfigConditions { Address = "10.0.0.1" } }]), Is.True);
            Assert.That(HydraConfig.HasLinkConditions([new HydraConfig { Mode = Mode.Master, Conditions = new ConfigConditions { GatewayMac = "a491b10c227e" } }]), Is.True);
            Assert.That(HydraConfig.HasLinkConditions([new HydraConfig { Mode = Mode.Master, Conditions = new ConfigConditions { Wired = false } }]), Is.True);
        }
    }

    [Test]
    public void Parse_ReadsTheThreeConditions()
    {
        var profile = HydraConfig.ParseAndValidate(AsFile($$"""
            [{"mode":"Master","conditions":{"address":"192.168.1.0/24","gatewayMac":"a4:91:b1:0c:22:7e","wired":true}{{Relay}}}]
            """))[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(profile.Conditions!.Address, Is.EqualTo("192.168.1.0/24"));
            Assert.That(profile.Conditions.GatewayMac, Is.EqualTo("a4:91:b1:0c:22:7e"));
            Assert.That(profile.Conditions.Wired, Is.True);
            Assert.That(profile.IsConditional, Is.True);
        }
    }

    [TestCase("192.168.1.7/24")]
    [TestCase("10")]
    [TestCase("192.168.1")]
    [TestCase("not an address")]
    [TestCase("192.168.1.0/33")]
    public void Validate_RefusesAMalformedAddress(string address)
    {
        var json = AsFile($$"""[{"mode":"Master","conditions":{"address":"{{address}}"}{{Relay}}}]""");
        Assert.That(() => HydraConfig.ParseAndValidate(json), Throws.InvalidOperationException.With.Message.Contains("address condition"));
    }

    [TestCase("a4:91:b1:0c:22")]
    [TestCase("a4:91:b1:0c:22:7g")]
    [TestCase("a4:91:b1:0c:22:7e7")]
    public void Validate_RefusesAMalformedGatewayMac(string mac)
    {
        var json = AsFile($$"""[{"mode":"Master","conditions":{"gatewayMac":"{{mac}}"}{{Relay}}}]""");
        Assert.That(() => HydraConfig.ParseAndValidate(json), Throws.InvalidOperationException.With.Message.Contains("gatewayMac condition"));
    }

    [TestCase("""{"gatewayMac":"a4:91:b1:0c:22:7e"}""", """{"gatewayMac":"A4-91-B1-0C-22-7E"}""")]
    [TestCase("""{"address":"10.0.0.7"}""", """{"address":"10.0.0.7/32"}""")]
    [TestCase("""{"wired":true}""", """{"wired":true}""")]
    public void Validate_RefusesDuplicateConditions_AsParsed(string first, string second)
    {
        var json = AsFile($$"""[{"mode":"Master","conditions":{{first}}{{Relay}}},{"mode":"Slave","conditions":{{second}}{{Relay}}}]""");
        Assert.That(() => HydraConfig.ParseAndValidate(json), Throws.InvalidOperationException.With.Message.Contains("duplicate conditions"));
    }

    [Test]
    public void Validate_AllowsWiredAndNotWired()
    {
        var json = AsFile($$"""[{"mode":"Master","conditions":{"wired":true}{{Relay}}},{"mode":"Slave","conditions":{"wired":false}{{Relay}}}]""");
        Assert.That(() => HydraConfig.ParseAndValidate(json), Throws.Nothing);
    }
}
