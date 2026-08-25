using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hydra.Management;

internal static class ConfigSecretMask
{
    internal const string Placeholder = "[hidden by Hydra TUI]";

    internal static string Mask(string json)
    {
        var node = JsonNode.Parse(json) ?? throw new JsonException("Configuration is empty.");
        Visit(node, (_, value) => value.ReplaceWith(Placeholder));
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    internal static string Restore(string editedJson, string sourceJson)
    {
        var edited = JsonNode.Parse(editedJson) ?? throw new JsonException("Configuration is empty.");
        var source = JsonNode.Parse(sourceJson) ?? throw new JsonException("Source configuration is empty.");
        RestoreNode(edited, source);
        return edited.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static void RestoreNode(JsonNode edited, JsonNode? source)
    {
        if (edited is JsonObject editedObject)
        {
            var sourceObject = source as JsonObject;
            foreach (var property in editedObject.ToList())
            {
                if (IsSecret(property.Key) && property.Value?.GetValueKind() == JsonValueKind.String
                    && property.Value.GetValue<string>() == Placeholder)
                {
                    var original = sourceObject?.FirstOrDefault(p => p.Key.Equals(property.Key, StringComparison.OrdinalIgnoreCase)).Value;
                    if (original != null) editedObject[property.Key] = original.DeepClone();
                    continue;
                }

                if (property.Value != null)
                {
                    var original = sourceObject?.FirstOrDefault(p => p.Key.Equals(property.Key, StringComparison.OrdinalIgnoreCase)).Value;
                    RestoreNode(property.Value, original);
                }
            }
        }
        else if (edited is JsonArray editedArray)
        {
            var sourceArray = source as JsonArray;
            for (var i = 0; i < editedArray.Count; i++)
                if (editedArray[i] != null)
                    RestoreNode(editedArray[i]!, sourceArray != null && i < sourceArray.Count ? sourceArray[i] : null);
        }
    }

    private static void Visit(JsonNode node, Action<string, JsonNode> secret)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToList())
            {
                if (property.Value == null) continue;
                if (IsSecret(property.Key) && property.Value.GetValueKind() == JsonValueKind.String)
                    secret(property.Key, property.Value);
                else
                    Visit(property.Value, secret);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
                if (item != null) Visit(item, secret);
        }
    }

    private static bool IsSecret(string name) =>
        name.Equals("password", StringComparison.OrdinalIgnoreCase)
        || name.Equals("networkConfig", StringComparison.OrdinalIgnoreCase);
}
