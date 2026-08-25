using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hydra.Management;

internal static class ManagementJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    internal static T Deserialize<T>(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? throw new InvalidOperationException("Request payload is required.")
            : JsonSerializer.Deserialize<T>(json, Options)
                ?? throw new InvalidOperationException("Request payload is invalid.");
}
