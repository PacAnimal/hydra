using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cathedral.Extensions;

namespace Hydra.Management;

internal sealed class GuidedConfigDocument
{
    private readonly JsonObject _root;

    private GuidedConfigDocument(JsonObject root) => _root = root;

    internal static GuidedConfigDocument Parse(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new JsonException("Configuration root must be a JSON object.");
        if (root.GetIgnoreCase("profiles") is not JsonArray)
            throw new JsonException("Configuration must contain a profiles array.");
        return new GuidedConfigDocument(root);
    }

    internal int ProfileCount => Profiles.Count;

    internal string ProfileLabel(int index)
    {
        var name = Profile(index).GetIgnoreCase(GuidedFields.ProfileName.Key)?.GetValue<string>();
        return string.IsNullOrWhiteSpace(name) ? $"Profile {index + 1}" : name;
    }

    internal GuidedValues ReadRoot() => Read(_root, GuidedFields.Root);

    internal void WriteRoot(GuidedValues values) => Write(_root, GuidedFields.Root, values);

    internal GuidedValues ReadProfile(int index) => Read(Profile(index), GuidedFields.Profile);

    internal void WriteProfile(int index, GuidedValues values) => Write(Profile(index), GuidedFields.Profile, values);

    internal GuidedTopology Topology(int index)
    {
        var profile = Profile(index);
        return new GuidedTopology(
            (profile.GetIgnoreCase("hosts") as JsonArray)?.Count ?? 0,
            (profile.GetIgnoreCase("screenDefinitions") as JsonArray)?.Count ?? 0);
    }

    internal string ToJson() => _root.ToIndentedJson() + Environment.NewLine;

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

    private JsonArray Profiles => (JsonArray)_root.GetIgnoreCase("profiles")!;
    private JsonObject Profile(int index) => Profiles[index] as JsonObject
        ?? throw new JsonException($"Profile {index + 1} must be a JSON object.");

    private static GuidedValues Read(JsonObject scope, IEnumerable<GuidedField> fields)
    {
        var values = new GuidedValues();
        foreach (var field in fields)
        {
            var owner = field.Parent == null ? scope : scope.GetIgnoreCase(field.Parent.Key) as JsonObject;
            values[field] = Format(field, owner?.GetIgnoreCase(field.Key));
        }
        return values;
    }

    private static string Format(GuidedField field, JsonNode? node) => node == null
        ? field.Fallback ?? ""
        : field.Kind switch
        {
            GuidedFieldKind.Toggle => node.GetValue<bool>() ? GuidedValues.On : GuidedValues.Off,
            GuidedFieldKind.Power => node.GetValue<bool>() ? "yes" : "no",
            _ when node.GetValueKind() == JsonValueKind.String => node.GetValue<string>(),
            GuidedFieldKind.Choice => ChoiceName(field, node) ?? node.ToJsonString(),
            _ => node.ToJsonString()
        };

    // the loader also takes an enum by number
    private static string? ChoiceName(GuidedField field, JsonNode node) =>
        field.ChoiceEnum != null && node.AsValue().TryGetValue<long>(out var number)
            ? Enum.GetName(field.ChoiceEnum, Enum.ToObject(field.ChoiceEnum, number))
            : null;

    // parses all of this scope's fields before touching it, so a bad field leaves the scope as it was
    private static void Write(JsonObject scope, IReadOnlyList<GuidedField> fields, GuidedValues values)
    {
        var parsed = fields.ToDictionary(f => f, f => Parse(f, values[f]));
        var objects = fields.Where(f => f.Parent != null).GroupBy(f => f.Parent!).ToList();
        foreach (var group in objects.Where(g => g.Key.AllOrNothing))
        {
            var set = group.Count(f => parsed[f] != null);
            if (set > 0 && set < group.Count())
                throw new InvalidOperationException(
                    $"The {group.Key.Label} needs {string.Join(" and ", group.Select(f => f.Label))} together; fill in all of them or clear them all.");
        }

        foreach (var field in fields)
        {
            var value = parsed[field];
            var owner = field.Parent == null ? scope : scope.GetIgnoreCase(field.Parent.Key) as JsonObject;
            if (owner == null)
            {
                if (value == null) continue;
                owner = [];
            }
            // also drops the parent's shadowed case-duplicates, which the loader would ignore anyway
            if (field.Parent != null) scope.SetIgnoreCase(field.Parent.Key, owner);
            if (value == null) owner.RemoveIgnoreCase(field.Key);
            else owner.SetIgnoreCase(field.Key, value);
        }

        // an emptied object goes; an all-or-nothing one also takes unknown keys with it, as it cannot load without its fields
        foreach (var group in objects)
            if (scope.GetIgnoreCase(group.Key.Key) is JsonObject owner && (owner.Count == 0 || group.Key.AllOrNothing && group.All(f => parsed[f] == null)))
                scope.RemoveIgnoreCase(group.Key.Key);
    }

    private static JsonNode? Parse(GuidedField field, string value)
    {
        var text = value.Trim();
        return field.Kind switch
        {
            GuidedFieldKind.Toggle => text == GuidedValues.On,
            GuidedFieldKind.Power => ParsePower(text),
            GuidedFieldKind.Integer => ParseInt(text, field.Label) is { } number ? JsonValue.Create(number) : null,
            GuidedFieldKind.Decimal => ParseDecimal(text, field.Label) is { } number ? JsonValue.Create(number) : null,
            _ when text.Length == 0 => null,
            GuidedFieldKind.Choice when !field.Choices.Any(c => c.EqualsIgnoreCase(text)) =>
                throw new InvalidOperationException($"{field.Label} must be one of: {string.Join(", ", field.Choices)}."),
            _ => text
        };
    }

    private static JsonNode? ParsePower(string value) => value.ToLowerInvariant() switch
    {
        "" or "any" => null,
        "yes" or "true" or "on" => true,
        "no" or "false" or "off" => false,
        _ => throw new InvalidOperationException("Power condition must be any, yes, or no.")
    };
}

/// <summary>The form's text for each field; toggles hold <see cref="On"/> or <see cref="Off"/>.</summary>
internal sealed class GuidedValues
{
    internal const string On = "true";
    internal const string Off = "false";

    private readonly Dictionary<GuidedField, string> _values = [];

    internal string this[GuidedField field]
    {
        get => _values.GetValueOrDefault(field) ?? "";
        set => _values[field] = value;
    }

    internal bool IsOn(GuidedField field) => this[field] == On;

    internal void Set(GuidedField field, bool on) => this[field] = on ? On : Off;
}

internal sealed record GuidedTopology(int HostCount, int ScreenDefinitionCount);
