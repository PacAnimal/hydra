using System.Text.Json;
using System.Text.Json.Nodes;
using Cathedral.Extensions;

namespace Hydra.Management;

internal static class ConfigSecretMask
{
    internal const string Placeholder = "[hidden by Hydra TUI]";

    internal static string Mask(string json)
    {
        var node = JsonNode.Parse(json) ?? throw new JsonException("Configuration is empty.");
        Visit(node, (_, value) => value.ReplaceWith(Placeholder));
        return node.ToIndentedJson();
    }

    internal static string Restore(string editedJson, string sourceJson)
    {
        var edited = JsonNode.Parse(editedJson) ?? throw new JsonException("Configuration is empty.");
        var source = JsonNode.Parse(sourceJson) ?? throw new JsonException("Source configuration is empty.");
        RestoreNode(edited, source);
        return edited.ToIndentedJson();
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
                    var original = sourceObject?.GetIgnoreCase(property.Key);
                    if (original != null) editedObject[property.Key] = original.DeepClone();
                    continue;
                }

                if (property.Value != null)
                {
                    var original = sourceObject?.GetIgnoreCase(property.Key);
                    RestoreNode(property.Value, original);
                }
            }
        }
        else if (edited is JsonArray editedArray)
        {
            for (var i = 0; i < editedArray.Count; i++)
                if (editedArray[i] != null)
                    RestoreNode(editedArray[i]!, source is JsonArray sourceArray && i < sourceArray.Count ? sourceArray[i] : null);
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
        name.EqualsIgnoreCase("password") || name.EqualsIgnoreCase("networkConfig");
}
