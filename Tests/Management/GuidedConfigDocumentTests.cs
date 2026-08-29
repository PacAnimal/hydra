using Hydra.Management;

namespace Tests.Management;

public class GuidedConfigDocumentTests
{
    [Test]
    public void FormEditsPreserveSecretsTopologyAndUnknownFields()
    {
        const string json = """
            {
              "name": "before",
              "unknownRoot": { "keep": true },
              "profiles": [{
                "profileName": "Home",
                "mode": "Master",
                "networkConfig": "secret",
                "hosts": [{ "name": "before", "neighbours": [{ "direction": "right", "name": "peer" }] }],
                "unknownProfile": 42
              }]
            }
            """;
        var document = GuidedConfigDocument.Parse(json);
        var root = document.ReadRoot() with { Name = "after", AutoUpdate = false };
        var profile = document.ReadProfile(0) with { Ssid = "Home WiFi", ScreenCount = 2, HideCursor = true, ClipboardSync = "System" };

        document.WriteRoot(root);
        document.WriteProfile(0, profile);
        var result = document.ToJson();

        using var parsed = System.Text.Json.JsonDocument.Parse(result);
        var rootJson = parsed.RootElement;
        var profileJson = rootJson.GetProperty("profiles")[0];
        Assert.Multiple(() =>
        {
            Assert.That(rootJson.GetProperty("name").GetString(), Is.EqualTo("after"));
            Assert.That(rootJson.GetProperty("unknownRoot").GetProperty("keep").GetBoolean(), Is.True);
            Assert.That(profileJson.GetProperty("networkConfig").GetString(), Is.EqualTo("secret"));
            Assert.That(profileJson.GetProperty("hosts")[0].GetProperty("neighbours").GetArrayLength(), Is.EqualTo(1));
            Assert.That(profileJson.GetProperty("unknownProfile").GetInt32(), Is.EqualTo(42));
            Assert.That(profileJson.GetProperty("conditions").GetProperty("ssid").GetString(), Is.EqualTo("Home WiFi"));
            Assert.That(profileJson.GetProperty("conditions").GetProperty("screenCount").GetInt32(), Is.EqualTo(2));
            Assert.That(profileJson.GetProperty("hideCursor").GetBoolean(), Is.True);
            Assert.That(profileJson.GetProperty("clipboardSync").GetString(), Is.EqualTo("System"));
        });
    }

    [Test]
    public void ClearingOptionalFieldsRemovesTheirObjects()
    {
        const string json = """{"profiles":[{"mode":"Slave","conditions":{"ssid":"x"},"embeddedStyx":{"server":"http://x","password":"pw"}}]}""";
        var document = GuidedConfigDocument.Parse(json);
        var profile = document.ReadProfile(0) with { Ssid = null, EmbeddedServer = null, EmbeddedPassword = null };

        document.WriteProfile(0, profile);
        var result = document.ToJson();

        Assert.Multiple(() =>
        {
            Assert.That(result, Does.Not.Contain("conditions"));
            Assert.That(result, Does.Not.Contain("embeddedStyx"));
        });
    }

    [TestCase("1.25", 1.25)]
    [TestCase("", null)]
    public void ParsesInvariantNumbers(string value, decimal? expected) =>
        Assert.That(GuidedConfigDocument.ParseDecimal(value, "scale"), Is.EqualTo(expected));

    [Test]
    public void RejectsInvalidNumbers() =>
        Assert.That(() => GuidedConfigDocument.ParseInt("1.5", "screen count"),
            Throws.InvalidOperationException.With.Message.Contains("whole number"));
}
