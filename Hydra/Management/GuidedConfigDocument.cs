using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hydra.Management;

internal sealed class GuidedConfigDocument
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };
    private readonly JsonObject _root;

    private GuidedConfigDocument(JsonObject root) => _root = root;

    internal static GuidedConfigDocument Parse(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new JsonException("Configuration root must be a JSON object.");
        if (root["profiles"] is not JsonArray)
            throw new JsonException("Configuration must contain a profiles array.");
        return new GuidedConfigDocument(root);
    }

    internal int ProfileCount => Profiles.Count;

    internal string ProfileLabel(int index)
    {
        var profile = Profile(index);
        var name = String(profile, "profileName");
        return string.IsNullOrWhiteSpace(name) ? $"Profile {index + 1}" : name;
    }

    internal GuidedRootFields ReadRoot() => new(
        String(_root, "name"),
        String(_root, "profile"),
        String(_root, "logLevel") ?? "info",
        Bool(_root, "autoUpdate", true),
        Bool(_root, "debugShield", false),
        Bool(_root, "debugMouse", false));

    internal void WriteRoot(GuidedRootFields fields)
    {
        SetOptionalString(_root, "name", fields.Name);
        SetOptionalString(_root, "profile", fields.ProfileOverride);
        SetOptionalString(_root, "logLevel", fields.LogLevel);
        _root["autoUpdate"] = fields.AutoUpdate;
        _root["debugShield"] = fields.DebugShield;
        _root["debugMouse"] = fields.DebugMouse;
    }

    internal GuidedProfileFields ReadProfile(int index)
    {
        var profile = Profile(index);
        var conditions = profile["conditions"] as JsonObject;
        var embedded = profile["embeddedStyx"] as JsonObject;
        var server = profile["embeddedStyxServer"] as JsonObject;
        return new GuidedProfileFields(
            String(profile, "profileName"),
            String(profile, "mode") ?? "Slave",
            conditions == null ? null : String(conditions, "ssid"),
            conditions == null ? null : Int(conditions, "screenCount"),
            conditions == null ? null : NullableBool(conditions, "isPluggedIn"),
            String(profile, "networkConfig"),
            embedded == null ? null : String(embedded, "server"),
            embedded == null ? null : String(embedded, "password"),
            server == null ? null : Int(server, "port"),
            server == null ? null : String(server, "password"),
            Bool(profile, "hideCursor", false),
            Bool(profile, "remoteOnly", false),
            String(profile, "clipboardSync") ?? "Hydra",
            Bool(profile, "syncScreensaver", true),
            Bool(profile, "screenLockPropagation", false),
            Bool(profile, "accelerateMouseWheel", true),
            Bool(profile, "unicodeKeyRepeat", true),
            Decimal(profile, "mouseScale"),
            Decimal(profile, "relativeMouseScale"),
            Int(profile, "deadCorners"),
            (profile["hosts"] as JsonArray)?.Count ?? 0,
            (profile["screenDefinitions"] as JsonArray)?.Count ?? 0);
    }

    internal void WriteProfile(int index, GuidedProfileFields fields)
    {
        var profile = Profile(index);
        SetOptionalString(profile, "profileName", fields.ProfileName);
        profile["mode"] = fields.Mode;
        SetConditions(profile, fields);
        SetOptionalString(profile, "networkConfig", fields.NetworkConfig);
        SetEmbeddedClient(profile, fields);
        SetEmbeddedServer(profile, fields);
        profile["hideCursor"] = fields.HideCursor;
        profile["remoteOnly"] = fields.RemoteOnly;
        profile["clipboardSync"] = fields.ClipboardSync;
        profile["syncScreensaver"] = fields.SyncScreensaver;
        profile["screenLockPropagation"] = fields.ScreenLockPropagation;
        profile["accelerateMouseWheel"] = fields.AccelerateMouseWheel;
        profile["unicodeKeyRepeat"] = fields.UnicodeKeyRepeat;
        SetOptionalNumber(profile, "mouseScale", fields.MouseScale);
        SetOptionalNumber(profile, "relativeMouseScale", fields.RelativeMouseScale);
        SetOptionalNumber(profile, "deadCorners", fields.DeadCorners);
    }

    internal string ToJson() => _root.ToJsonString(PrettyJson) + Environment.NewLine;

    internal static decimal? ParseDecimal(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        throw new InvalidOperationException($"{field} must be a number.");
    }

    internal static int? ParseInt(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        throw new InvalidOperationException($"{field} must be a whole number.");
    }

    private JsonArray Profiles => (JsonArray)_root["profiles"]!;
    private JsonObject Profile(int index) => Profiles[index] as JsonObject
        ?? throw new JsonException($"Profile {index + 1} must be a JSON object.");

    private static string? String(JsonObject node, string name) => node[name]?.GetValue<string>();
    private static bool Bool(JsonObject node, string name, bool fallback) => node[name]?.GetValue<bool>() ?? fallback;
    private static bool? NullableBool(JsonObject node, string name) => node[name]?.GetValue<bool>();
    private static int? Int(JsonObject node, string name) => node[name]?.GetValue<int>();
    private static decimal? Decimal(JsonObject node, string name) => node[name]?.GetValue<decimal>();

    private static void SetOptionalString(JsonObject node, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) node.Remove(name);
        else node[name] = value.Trim();
    }

    private static void SetOptionalNumber<T>(JsonObject node, string name, T? value) where T : struct
    {
        if (value == null) node.Remove(name);
        else node[name] = JsonValue.Create(value.Value);
    }

    private static void SetConditions(JsonObject profile, GuidedProfileFields fields)
    {
        if (string.IsNullOrWhiteSpace(fields.Ssid) && fields.ScreenCount == null && fields.IsPluggedIn == null)
        {
            profile.Remove("conditions");
            return;
        }
        var conditions = profile["conditions"] as JsonObject ?? [];
        SetOptionalString(conditions, "ssid", fields.Ssid);
        SetOptionalNumber(conditions, "screenCount", fields.ScreenCount);
        if (fields.IsPluggedIn == null) conditions.Remove("isPluggedIn");
        else conditions["isPluggedIn"] = fields.IsPluggedIn.Value;
        profile["conditions"] = conditions;
    }

    private static void SetEmbeddedClient(JsonObject profile, GuidedProfileFields fields)
    {
        if (string.IsNullOrWhiteSpace(fields.EmbeddedServer) && string.IsNullOrWhiteSpace(fields.EmbeddedPassword))
        {
            profile.Remove("embeddedStyx");
            return;
        }
        var embedded = profile["embeddedStyx"] as JsonObject ?? [];
        SetOptionalString(embedded, "server", fields.EmbeddedServer);
        SetOptionalString(embedded, "password", fields.EmbeddedPassword);
        profile["embeddedStyx"] = embedded;
    }

    private static void SetEmbeddedServer(JsonObject profile, GuidedProfileFields fields)
    {
        if (fields.EmbeddedPort == null && string.IsNullOrWhiteSpace(fields.EmbeddedServerPassword))
        {
            profile.Remove("embeddedStyxServer");
            return;
        }
        var server = profile["embeddedStyxServer"] as JsonObject ?? [];
        SetOptionalNumber(server, "port", fields.EmbeddedPort);
        SetOptionalString(server, "password", fields.EmbeddedServerPassword);
        profile["embeddedStyxServer"] = server;
    }
}

internal sealed record GuidedRootFields(string? Name, string? ProfileOverride, string LogLevel, bool AutoUpdate, bool DebugShield, bool DebugMouse);

internal sealed record GuidedProfileFields(
    string? ProfileName, string Mode, string? Ssid, int? ScreenCount, bool? IsPluggedIn,
    string? NetworkConfig, string? EmbeddedServer, string? EmbeddedPassword, int? EmbeddedPort,
    string? EmbeddedServerPassword, bool HideCursor, bool RemoteOnly, string ClipboardSync, bool SyncScreensaver,
    bool ScreenLockPropagation, bool AccelerateMouseWheel, bool UnicodeKeyRepeat, decimal? MouseScale,
    decimal? RelativeMouseScale, int? DeadCorners, int HostCount, int ScreenDefinitionCount);
