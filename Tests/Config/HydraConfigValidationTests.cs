using Hydra.Config;
using static Tests.Setup.ConfigJson;

namespace Tests.Config;

[TestFixture]
public class HydraConfigValidationTests
{
    // ─── modifierNumLock ──────────────────────────────────────────────────────

    [TestCase("true")]
    [TestCase("false")]
    public void Validate_ModifierNumLock_OnSlave_Throws(string value)
    {
        var json = AsFile($$"""[{"mode":"Slave","embeddedStyx":{"server":"https://localhost:5000","password":"pw"},"modifierNumLock":{{value}}}]""");
        var ex = Assert.Throws<InvalidOperationException>(() => HydraConfig.ParseAndValidate(json));
        Assert.That(ex!.Message, Does.Contain("modifierNumLock is master-only"));
    }

    [TestCase("", null)]
    [TestCase(",\"modifierNumLock\":null", null)]
    [TestCase(",\"modifierNumLock\":true", true)]
    [TestCase(",\"modifierNumLock\":false", false)]
    public void ModifierNumLock_OnMaster_IsReadAsGiven_NullByDefault(string field, bool? expected)
    {
        var json = AsFile($$"""[{"mode":"Master","embeddedStyx":{"server":"https://localhost:5000","password":"pw"}{{field}}}]""");
        Assert.That(HydraConfig.ParseAndValidate(json)[0].ModifierNumLock, Is.EqualTo(expected));
    }

    // ─── maxMouseHz ───────────────────────────────────────────────────────────

    [Test]
    public void Validate_MaxMouseHz_OnSlave_Throws()
    {
        var json = AsFile("""[{"mode":"Slave","embeddedStyx":{"server":"https://localhost:5000","password":"pw"},"maxMouseHz":250}]""");
        var ex = Assert.Throws<InvalidOperationException>(() => HydraConfig.ParseAndValidate(json));
        Assert.That(ex!.Message, Does.Contain("maxMouseHz is master-only"));
    }

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(1001)]
    public void Validate_MaxMouseHz_OutOfRange_Throws(int hz)
    {
        var json = AsFile($$"""[{"mode":"Master","embeddedStyx":{"server":"https://localhost:5000","password":"pw"},"maxMouseHz":{{hz}}}]""");
        var ex = Assert.Throws<InvalidOperationException>(() => HydraConfig.ParseAndValidate(json));
        Assert.That(ex!.Message, Does.Contain("maxMouseHz must be between 1 and 1000"));
    }

    [TestCase(1)]
    [TestCase(250)]
    [TestCase(1000)]
    public void Validate_MaxMouseHz_InRange_Passes(int hz)
    {
        var json = AsFile($$"""[{"mode":"Master","embeddedStyx":{"server":"https://localhost:5000","password":"pw"},"maxMouseHz":{{hz}}}]""");
        Assert.DoesNotThrow(() => HydraConfig.ParseAndValidate(json));
    }

    // ─── relay requirement ────────────────────────────────────────────────────

    [Test]
    public void Validate_NoRelay_Throws()
    {
        var json = AsFile("""[{"mode":"Master"}]""");
        var ex = Assert.Throws<InvalidOperationException>(() => HydraConfig.ParseAndValidate(json));
        Assert.That(ex!.Message, Does.Contain("no relay configured"));
    }

    [Test]
    public void Validate_WithNetworkConfig_Passes()
    {
        var json = AsFile("""[{"mode":"Master","networkConfig":"embedded|http://localhost:5000|pw"}]""");
        Assert.DoesNotThrow(() => HydraConfig.ParseAndValidate(json));
    }

    [Test]
    public void Validate_WithEmbeddedStyx_Passes()
    {
        var json = AsFile("""[{"mode":"Master","embeddedStyx":{"server":"https://localhost:5000","password":"pw"}}]""");
        Assert.DoesNotThrow(() => HydraConfig.ParseAndValidate(json));
    }

    [TestCase("http://localhost:5000")]
    [TestCase("ws://localhost:5000")]
    [TestCase("tcp://localhost:5000")]
    public void Validate_EmbeddedStyx_NotHttps_Throws(string server)
    {
        var json = AsFile($$$"""[{"mode":"Master","embeddedStyx":{"server":"{{{server}}}","password":"pw"}}]""");
        var ex = Assert.Throws<InvalidOperationException>(() => HydraConfig.ParseAndValidate(json));
        Assert.That(ex!.Message, Does.Contain("embeddedStyx.server must be https://"));
    }

    [TestCase("https://localhost:5000")]
    [TestCase("HTTPS://localhost:5000")]
    [TestCase("localhost:5000")]
    public void Validate_EmbeddedStyx_HttpsOrSchemeless_Passes(string server)
    {
        var json = AsFile($$$"""[{"mode":"Master","embeddedStyx":{"server":"{{{server}}}","password":"pw"}}]""");
        Assert.DoesNotThrow(() => HydraConfig.ParseAndValidate(json));
    }

    [Test]
    public void Validate_WithEmbeddedStyxServer_Passes()
    {
        var json = AsFile("""[{"mode":"Master","embeddedStyxServer":{"port":5000,"password":"pw"}}]""");
        Assert.DoesNotThrow(() => HydraConfig.ParseAndValidate(json));
    }

    [Test]
    public void Validate_MultipleProfiles_AllRequireRelay()
    {
        var json = AsFile("""
            [
              {"mode":"Master","embeddedStyx":{"server":"https://localhost:5000","password":"pw"},"conditions":{"ssid":"home"}},
              {"mode":"Master"}
            ]
            """);
        var ex = Assert.Throws<InvalidOperationException>(() => HydraConfig.ParseAndValidate(json));
        Assert.That(ex!.Message, Does.Contain("no relay configured"));
    }
}
