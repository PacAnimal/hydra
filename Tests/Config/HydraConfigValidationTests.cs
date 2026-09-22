using Hydra.Config;

namespace Tests.Config;

[TestFixture]
public class HydraConfigValidationTests
{
    private static string AsFile(string profilesJson) => $$"""{"profiles":{{profilesJson}}}""";

    // ─── maxMouseHz ───────────────────────────────────────────────────────────

    [Test]
    public void Validate_MaxMouseHz_OnSlave_Throws()
    {
        var json = AsFile("""[{"mode":"Slave","embeddedStyx":{"server":"http://localhost:5000","password":"pw"},"maxMouseHz":250}]""");
        var ex = Assert.Throws<InvalidOperationException>(() => HydraConfig.ParseAndValidate(json));
        Assert.That(ex!.Message, Does.Contain("maxMouseHz is master-only"));
    }

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(1001)]
    public void Validate_MaxMouseHz_OutOfRange_Throws(int hz)
    {
        var json = AsFile($$"""[{"mode":"Master","embeddedStyx":{"server":"http://localhost:5000","password":"pw"},"maxMouseHz":{{hz}}}]""");
        var ex = Assert.Throws<InvalidOperationException>(() => HydraConfig.ParseAndValidate(json));
        Assert.That(ex!.Message, Does.Contain("maxMouseHz must be between 1 and 1000"));
    }

    [TestCase(1)]
    [TestCase(250)]
    [TestCase(1000)]
    public void Validate_MaxMouseHz_InRange_Passes(int hz)
    {
        var json = AsFile($$"""[{"mode":"Master","embeddedStyx":{"server":"http://localhost:5000","password":"pw"},"maxMouseHz":{{hz}}}]""");
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
        var json = AsFile("""[{"mode":"Master","embeddedStyx":{"server":"http://localhost:5000","password":"pw"}}]""");
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
              {"mode":"Master","embeddedStyx":{"server":"http://localhost:5000","password":"pw"},"conditions":{"ssid":"home"}},
              {"mode":"Master"}
            ]
            """);
        var ex = Assert.Throws<InvalidOperationException>(() => HydraConfig.ParseAndValidate(json));
        Assert.That(ex!.Message, Does.Contain("no relay configured"));
    }
}
