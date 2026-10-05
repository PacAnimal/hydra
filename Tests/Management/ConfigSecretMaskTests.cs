using System.Text.Json.Nodes;
using Cathedral.Extensions;
using Hydra.Config;
using Hydra.Management;

namespace Tests.Management;

public class ConfigSecretMaskTests
{
    private const string Source = """
        {
          "unknown": { "keep": true },
          "profiles": [{
            "networkConfig": "relay-secret",
            "embeddedStyx": { "server": "http://localhost:5000", "password": "password-secret" }
          }]
        }
        """;

    [Test]
    public void Mask_HidesAllSupportedSecrets()
    {
        var masked = ConfigSecretMask.Mask(Source);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(masked, Does.Not.Contain("relay-secret"));
            Assert.That(masked, Does.Not.Contain("password-secret"));
            Assert.That(masked, Does.Contain(ConfigSecretMask.Placeholder));
            Assert.That(masked, Does.Contain("unknown"));
        }
    }

    [Test]
    public void Restore_PreservesHiddenValuesAndAcceptsExplicitReplacement()
    {
        var edited = ConfigSecretMask.Mask(Source).Replace(
            $"\"password\": \"{ConfigSecretMask.Placeholder}\"",
            "\"password\": \"replacement\"");

        var restored = JsonNode.Parse(ConfigSecretMask.Restore(edited, Source))!;
        var profile = restored["profiles"]![0]!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(profile["networkConfig"]!.GetValue<string>(), Is.EqualTo("relay-secret"));
            Assert.That(profile["embeddedStyx"]!["password"]!.GetValue<string>(), Is.EqualTo("replacement"));
            Assert.That(restored["unknown"]!["keep"]!.GetValue<bool>(), Is.True);
        }
    }

    [Test]
    public void Restore_PutsBackTheValueTheLoaderUses_WhenKeysDifferOnlyInCase()
    {
        const string source = """{"profiles":[{"mode":"Slave","networkConfig":"shadowed","NetworkConfig":"effective"}]}""";

        var restored = ConfigSecretMask.Restore(ConfigSecretMask.Mask(source), source);

        Assert.That(restored.FromSaneJson<HydraConfigFile>()!.Profiles[0].NetworkConfig, Is.EqualTo("effective"));
    }
}
