using System.Text.Json;
using System.Text.Json.Nodes;
using Cathedral.Extensions;

namespace Hydra.Management;

/// <summary>
/// Key lookup that agrees with the config loader, which matches property names case-insensitively and lets
/// the last of several case-variant duplicates win.
/// </summary>
internal static class JsonNodeExt
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    internal static string ToIndentedJson(this JsonNode node) => node.ToJsonString(Indented);

    internal static JsonNode? GetIgnoreCase(this JsonObject node, string name) =>
        node.LastOrDefault(p => p.Key.EqualsIgnoreCase(name)).Value;

    // replaces the effective key in place, keeping its spelling; shadowed duplicates go
    internal static void SetIgnoreCase(this JsonObject node, string name, JsonNode? value)
    {
        var keys = Keys(node, name);
        if (keys.Count == 0)
        {
            node[name] = value;
            return;
        }
        foreach (var shadowed in keys[..^1]) node.Remove(shadowed);
        if (!ReferenceEquals(node[keys[^1]], value)) node[keys[^1]] = value;
    }

    internal static void RemoveIgnoreCase(this JsonObject node, string name)
    {
        foreach (var key in Keys(node, name)) node.Remove(key);
    }

    private static List<string> Keys(JsonObject node, string name) => [.. node.Select(p => p.Key).Where(k => k.EqualsIgnoreCase(name))];
}
