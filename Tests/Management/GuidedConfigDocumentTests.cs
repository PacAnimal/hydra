using System.Text.Json.Nodes;
using Cathedral.Extensions;
using Hydra.Config;
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
        var root = document.ReadRoot();
        root[GuidedFields.Name] = "after";
        root.Set(GuidedFields.AutoUpdate, false);
        var profile = document.ReadProfile(0);
        profile[GuidedFields.Ssid] = "Home WiFi";
        profile[GuidedFields.ScreenCount] = "2";
        profile.Set(GuidedFields.HideCursor, true);

        document.WriteRoot(root);
        document.WriteProfile(0, profile);
        var result = document.ToJson();

        using var parsed = System.Text.Json.JsonDocument.Parse(result);
        var rootJson = parsed.RootElement;
        var profileJson = rootJson.GetProperty("profiles")[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rootJson.GetProperty("name").GetString(), Is.EqualTo("after"));
            Assert.That(rootJson.GetProperty("unknownRoot").GetProperty("keep").GetBoolean(), Is.True);
            Assert.That(profileJson.GetProperty("networkConfig").GetString(), Is.EqualTo("secret"));
            Assert.That(profileJson.GetProperty("hosts")[0].GetProperty("neighbours").GetArrayLength(), Is.EqualTo(1));
            Assert.That(profileJson.GetProperty("unknownProfile").GetInt32(), Is.EqualTo(42));
            Assert.That(profileJson.GetProperty("conditions").GetProperty("ssid").GetString(), Is.EqualTo("Home WiFi"));
            Assert.That(profileJson.GetProperty("conditions").GetProperty("screenCount").GetInt32(), Is.EqualTo(2));
            Assert.That(profileJson.GetProperty("hideCursor").GetBoolean(), Is.True);
        }
    }

    [Test]
    public void ClearingOptionalFieldsRemovesTheirObjects()
    {
        const string json = """{"profiles":[{"mode":"Slave","conditions":{"ssid":"x"},"embeddedStyx":{"server":"http://x","password":"pw"}}]}""";
        var document = GuidedConfigDocument.Parse(json);
        var profile = document.ReadProfile(0);
        profile[GuidedFields.Ssid] = "";
        profile[GuidedFields.EmbeddedServer] = "";
        profile[GuidedFields.EmbeddedPassword] = "";

        document.WriteProfile(0, profile);
        var result = document.ToJson();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Does.Not.Contain("conditions"));
            Assert.That(result, Does.Not.Contain("embeddedStyx"));
        }
    }

    [Test]
    public void HandEditedKeysInAnyCaseShowInTheFormAndSaveInPlace()
    {
        const string json = """{"AutoUpdate":false,"Profiles":[{"Mode":"Master","HideCursor":true,"MouseScale":1.5,"Conditions":{"SSID":"Home"}}]}""";
        var document = GuidedConfigDocument.Parse(json);
        var root = document.ReadRoot();
        var profile = document.ReadProfile(0);

        document.WriteRoot(root);
        document.WriteProfile(0, profile);
        var result = JsonNode.Parse(document.ToJson())!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(root.IsOn(GuidedFields.AutoUpdate), Is.False);
            Assert.That(profile[GuidedFields.Mode], Is.EqualTo("Master"));
            Assert.That(profile.IsOn(GuidedFields.HideCursor), Is.True);
            Assert.That(profile[GuidedFields.MouseScale], Is.EqualTo("1.5"));
            Assert.That(profile[GuidedFields.Ssid], Is.EqualTo("Home"));
            Assert.That(KeysOf(result), Is.EquivalentTo(["AutoUpdate", "Profiles", "logLevel", "debugShield", "debugMouse"]));
            Assert.That(KeysOf(result["Profiles"]![0]!), Does.Contain("Mode").And.Contain("HideCursor").And.Contain("MouseScale").And.Contain("Conditions"));
            Assert.That(KeysOf(result["Profiles"]![0]!).Select(k => k.ToLowerInvariant()), Is.Unique);
            Assert.That(KeysOf(result["Profiles"]![0]!["Conditions"]!), Is.EquivalentTo(["SSID"]));
            Assert.That(result["Profiles"]![0]!["HideCursor"]!.GetValue<bool>(), Is.True);
        }
    }

    [Test]
    public void EditingAHandEditedKeyKeepsItsSpelling()
    {
        var document = GuidedConfigDocument.Parse("""{"profiles":[{"mode":"Master","HideCursor":true,"Conditions":{"SSID":"Home"}}]}""");

        var edited = document.ReadProfile(0);
        edited.Set(GuidedFields.HideCursor, false);
        edited[GuidedFields.Ssid] = "Work";
        document.WriteProfile(0, edited);
        var profile = JsonNode.Parse(document.ToJson())!["profiles"]![0]!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(profile["HideCursor"]!.GetValue<bool>(), Is.False);
            Assert.That(profile["hideCursor"], Is.Null);
            Assert.That(profile["Conditions"]!["SSID"]!.GetValue<string>(), Is.EqualTo("Work"));
            Assert.That(profile["conditions"], Is.Null);
        }
    }

    [Test]
    public void DuplicateKeysShowAndKeepTheValueTheLoaderUses()
    {
        const string relay = """ "embeddedStyx":{"server":"http://x:5000","password":"pw"} """;
        var document = GuidedConfigDocument.Parse($$"""{"profiles":[{"mode":"Master","hideCursor":false,"HideCursor":true,{{relay}}}]}""");

        var shown = document.ReadProfile(0);
        document.WriteProfile(0, shown);
        var loaded = HydraConfig.ParseAndValidate(document.ToJson());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(shown.IsOn(GuidedFields.HideCursor), Is.True);
            Assert.That(loaded[0].HideCursor, Is.True);
            Assert.That(KeysOf(JsonNode.Parse(document.ToJson())!["profiles"]![0]!).Count(k => k.EqualsIgnoreCase("hideCursor")), Is.EqualTo(1));
        }
    }

    [Test]
    public void DuplicateParentObjectsKeepOnlyTheOneTheLoaderUses()
    {
        var document = GuidedConfigDocument.Parse("""{"profiles":[{"mode":"Slave","conditions":{"ssid":"Old"},"Conditions":{"ssid":"Home"}}]}""");

        var shown = document.ReadProfile(0);
        shown[GuidedFields.Ssid] = "Work";
        document.WriteProfile(0, shown);
        var profile = JsonNode.Parse(document.ToJson())!["profiles"]![0]!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(KeysOf(profile).Where(k => k.EqualsIgnoreCase("conditions")), Is.EqualTo(["Conditions"]));
            Assert.That(profile["Conditions"]!["ssid"]!.GetValue<string>(), Is.EqualTo("Work"));
        }
    }

    // the loader's enum converter takes numbers too
    [Test]
    public void NumericEnumValuesShowAsTheirNames()
    {
        var document = GuidedConfigDocument.Parse("""{"profiles":[{"mode":0,"clipboardSync":1}]}""");

        var profile = document.ReadProfile(0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(profile[GuidedFields.Mode], Is.EqualTo(nameof(Mode.Master)));
            Assert.That(profile[GuidedFields.ClipboardSync], Is.EqualTo(nameof(ClipboardSyncMode.System)));
        }
    }

    [Test]
    public void AnUndefinedNumericEnumValueShowsAsWritten() =>
        Assert.That(GuidedConfigDocument.Parse("""{"profiles":[{"clipboardSync":7}]}""").ReadProfile(0)[GuidedFields.ClipboardSync], Is.EqualTo("7"));

    // every field the form edits, set away from its default
    private const string EveryField = """
        {
          "name": "desk",
          "profile": "Home",
          "logLevel": "dbug",
          "autoUpdate": false,
          "debugShield": true,
          "debugMouse": true,
          "profiles": [{
            "profileName": "Home",
            "mode": "Slave",
            "conditions": { "ssid": "Home WiFi", "screenCount": 2, "isPluggedIn": true },
            "networkConfig": "secret",
            "embeddedStyx": { "server": "http://relay:5000", "password": "client-pw" },
            "embeddedStyxServer": { "port": 5001, "password": "server-pw" },
            "hideCursor": true,
            "remoteOnly": true,
            "syncScreensaver": false,
            "screenLockPropagation": true,
            "accelerateMouseWheel": false,
            "unicodeKeyRepeat": false,
            "clipboardSync": "System",
            "allowSystemSleep": true,
            "mouseScale": 1.5,
            "relativeMouseScale": 0.75,
            "deadCorners": 12,
            "maxMouseHz": 250
          }]
        }
        """;

    [Test]
    public void ReadingAndWritingBackEveryFieldLeavesTheDocumentUnchanged()
    {
        var document = GuidedConfigDocument.Parse(EveryField);
        var root = document.ReadRoot();
        var profile = document.ReadProfile(0);

        document.WriteRoot(root);
        document.WriteProfile(0, profile);

        using (Assert.EnterMultipleScope())
        {
            foreach (var field in GuidedFields.All)
                Assert.That((field.IsRoot ? root : profile)[field], Is.EqualTo(Samples[field]), field.Key);
            Assert.That(JsonNode.DeepEquals(JsonNode.Parse(document.ToJson()), JsonNode.Parse(EveryField)), Is.True, document.ToJson());
        }
    }

    [Test]
    public void ClearingANestedObjectsFieldsKeepsItsUnknownKeys()
    {
        var document = GuidedConfigDocument.Parse("""{"profiles":[{"mode":"Slave","conditions":{"ssid":"x","note":"keep"}}]}""");
        var profile = document.ReadProfile(0);
        profile[GuidedFields.Ssid] = "";

        document.WriteProfile(0, profile);

        Assert.That(JsonNode.Parse(document.ToJson())!["profiles"]![0]!["conditions"]!.ToJsonString(), Is.EqualTo("""{"note":"keep"}"""));
    }

    [Test]
    public void ClearingAnEmbeddedRelayDropsItWhole_SinceItCannotLoadWithoutItsFields()
    {
        var document = GuidedConfigDocument.Parse("""{"profiles":[{"mode":"Slave","embeddedStyx":{"server":"http://x","password":"pw","note":"x"}}]}""");
        var profile = document.ReadProfile(0);
        profile[GuidedFields.EmbeddedServer] = "";
        profile[GuidedFields.EmbeddedPassword] = "";

        document.WriteProfile(0, profile);

        Assert.That(document.ToJson(), Does.Not.Contain("embeddedStyx"));
    }

    [TestCase("embeddedStyx", "server", "Embedded URL and Password")]
    [TestCase("embeddedStyx", "password", "Embedded URL and Password")]
    [TestCase("embeddedStyxServer", "port", "Local Port and Password")]
    [TestCase("embeddedStyxServer", "password", "Local Port and Password")]
    public void APartialEmbeddedRelayIsRefusedWithoutTouchingTheDocument(string parent, string key, string message)
    {
        var field = GuidedFields.All.Single(f => f.Parent?.Key == parent && f.Key == key);
        var document = GuidedConfigDocument.Parse("""{"profiles":[{"mode":"Slave"}]}""");
        var before = document.ToJson();
        var profile = document.ReadProfile(0);
        profile[field] = field.Kind == GuidedFieldKind.Integer ? "5000" : "value";

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => document.WriteProfile(0, profile), Throws.InvalidOperationException.With.Message.Contains(message));
            Assert.That(document.ToJson(), Is.EqualTo(before));
        }
    }

    [Test]
    public void AnUnknownChoiceIsRefused()
    {
        var document = GuidedConfigDocument.Parse("""{"profiles":[{"mode":"Slave"}]}""");
        var profile = document.ReadProfile(0);
        profile[GuidedFields.ClipboardSync] = "Automatic";

        Assert.That(() => document.WriteProfile(0, profile),
            Throws.InvalidOperationException.With.Message.EqualTo("Clipboard must be one of: Hydra, System."));
    }

    // mode is required, so an absent one stays absent for the validator to refuse rather than becoming a slave
    [Test]
    public void AnAbsentModeIsNotWrittenBackAsSlave()
    {
        var document = GuidedConfigDocument.Parse("""{"profiles":[{"hideCursor":true}]}""");
        var profile = document.ReadProfile(0);

        document.WriteProfile(0, profile);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(profile[GuidedFields.Mode], Is.Empty);
            Assert.That(document.ToJson(), Does.Not.Contain("mode").IgnoreCase);
        }
    }

    // values the loader would refuse too, named on save rather than thrown from deep inside it
    [TestCase("""{"profiles":[{"mode":"Slave","clipboardSync":7}]}""", "Clipboard must be one of: Hydra, System.")]
    [TestCase("""{"profiles":[{"mode":9}]}""", "Mode must be one of: Master, Slave.")]
    [TestCase("""{"profiles":[{"mode":"Slave","deadCorners":5.0}]}""", "Dead Corners must be a whole number.")]
    public void AValueTheFormCannotWriteIsRefusedByName(string json, string message)
    {
        var document = GuidedConfigDocument.Parse(json);
        var before = document.ToJson();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => document.WriteProfile(0, document.ReadProfile(0)), Throws.InvalidOperationException.With.Message.EqualTo(message));
            Assert.That(document.ToJson(), Is.EqualTo(before));
        }
    }

    [Test]
    public void AnEmptyClipboardSyncLeavesTheDefaultToTheLoader()
    {
        var document = GuidedConfigDocument.Parse("""{"profiles":[{"mode":"Slave","clipboardSync":"System"}]}""");
        var profile = document.ReadProfile(0);
        profile[GuidedFields.ClipboardSync] = "";

        document.WriteProfile(0, profile);

        Assert.That(document.ToJson(), Does.Not.Contain("clipboardSync"));
    }

    [Test]
    public void EveryFieldWritesWhatTheLoaderReads()
    {
        var document = GuidedConfigDocument.Parse("""{"profiles":[{"mode":"Master"}]}""");

        var values = new GuidedValues();
        foreach (var field in GuidedFields.All) values[field] = Samples[field];

        document.WriteRoot(values);
        document.WriteProfile(0, values);
        var file = document.ToJson().FromSaneJson<HydraConfigFile>()!;
        var profile = file.Profiles[0];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(file.Name, Is.EqualTo("desk"));
            Assert.That(file.Profile, Is.EqualTo("Home"));
            Assert.That(file.LogLevel, Is.EqualTo(Microsoft.Extensions.Logging.LogLevel.Debug));
            Assert.That(file.AutoUpdate, Is.False);
            Assert.That(file.DebugShield, Is.True);
            Assert.That(file.DebugMouse, Is.True);
            Assert.That(profile.ProfileName, Is.EqualTo("Home"));
            Assert.That(profile.Mode, Is.EqualTo(Mode.Slave));
            Assert.That(profile.Conditions!.Ssid, Is.EqualTo("Home WiFi"));
            Assert.That(profile.Conditions.ScreenCount, Is.EqualTo(2));
            Assert.That(profile.Conditions.IsPluggedIn, Is.True);
            Assert.That(profile.NetworkConfig, Is.EqualTo("secret"));
            Assert.That(profile.EmbeddedStyx!.Server, Is.EqualTo("http://relay:5000"));
            Assert.That(profile.EmbeddedStyx.Password, Is.EqualTo("client-pw"));
            Assert.That(profile.EmbeddedStyxServer!.Port, Is.EqualTo(5001));
            Assert.That(profile.EmbeddedStyxServer.Password, Is.EqualTo("server-pw"));
            Assert.That(profile.HideCursor, Is.True);
            Assert.That(profile.RemoteOnly, Is.True);
            Assert.That(profile.SyncScreensaver, Is.False);
            Assert.That(profile.ScreenLockPropagation, Is.True);
            Assert.That(profile.AccelerateMouseWheel, Is.False);
            Assert.That(profile.UnicodeKeyRepeat, Is.False);
            Assert.That(profile.AllowSystemSleep, Is.True);
            Assert.That(profile.ClipboardSync, Is.EqualTo(ClipboardSyncMode.System));
            Assert.That(profile.MouseScale, Is.EqualTo(1.5m));
            Assert.That(profile.RelativeMouseScale, Is.EqualTo(0.75m));
            Assert.That(profile.DeadCorners, Is.EqualTo(12));
            Assert.That(profile.MaxMouseHz, Is.EqualTo(250));
        }
    }

    // what a user would type into each field; a field missing here fails the test above
    private static readonly Dictionary<GuidedField, string> Samples = new()
    {
        [GuidedFields.Name] = "desk",
        [GuidedFields.ProfileOverride] = "Home",
        [GuidedFields.LogLevel] = "dbug",
        [GuidedFields.AutoUpdate] = GuidedValues.Off,
        [GuidedFields.DebugShield] = GuidedValues.On,
        [GuidedFields.DebugMouse] = GuidedValues.On,
        [GuidedFields.ProfileName] = "Home",
        [GuidedFields.Mode] = "Slave",
        [GuidedFields.Ssid] = "Home WiFi",
        [GuidedFields.ScreenCount] = "2",
        [GuidedFields.IsPluggedIn] = "yes",
        [GuidedFields.NetworkConfig] = "secret",
        [GuidedFields.EmbeddedServer] = "http://relay:5000",
        [GuidedFields.EmbeddedPassword] = "client-pw",
        [GuidedFields.EmbeddedPort] = "5001",
        [GuidedFields.EmbeddedServerPassword] = "server-pw",
        [GuidedFields.HideCursor] = GuidedValues.On,
        [GuidedFields.RemoteOnly] = GuidedValues.On,
        [GuidedFields.SyncScreensaver] = GuidedValues.Off,
        [GuidedFields.ScreenLockPropagation] = GuidedValues.On,
        [GuidedFields.AccelerateMouseWheel] = GuidedValues.Off,
        [GuidedFields.UnicodeKeyRepeat] = GuidedValues.Off,
        [GuidedFields.AllowSystemSleep] = GuidedValues.On,
        [GuidedFields.ClipboardSync] = "System",
        [GuidedFields.MouseScale] = "1.5",
        [GuidedFields.RelativeMouseScale] = "0.75",
        [GuidedFields.DeadCorners] = "12",
        [GuidedFields.MaxMouseHz] = "250"
    };

    private static IEnumerable<string> KeysOf(JsonNode node) => node.AsObject().Select(p => p.Key);

    [TestCase("1.25", 1.25)]
    [TestCase("", null)]
    public void ParsesInvariantNumbers(string value, decimal? expected) =>
        Assert.That(GuidedConfigDocument.ParseDecimal(value, "scale"), Is.EqualTo(expected));

    [Test]
    public void RejectsInvalidNumbers() =>
        Assert.That(() => GuidedConfigDocument.ParseInt("1.5", "screen count"),
            Throws.InvalidOperationException.With.Message.Contains("whole number"));
}
