using System.Text.Json;
using System.Text.Json.Serialization;
using Cathedral.Extensions;
using Hydra.Screen;
using Microsoft.Extensions.Logging;

namespace Hydra.Config;

public class ScreenConfig
{
    public required string Name { get; init; }
    // fake always-connected debug screen — no real slave needed
    public bool IsVirtual { get; init; }
    public List<NeighbourConfig> Neighbours { get; init; } = [];
}

public class NeighbourConfig
{
    public required Direction Direction { get; init; }
    public required string Name { get; init; }
    public decimal Scale { get; init; } = 1.0m;
    public int Offset { get; init; }
}

public class HydraConfig
{
    public required Mode Mode { get; init; }
    // master only — ignored in slave mode
    public List<ScreenConfig> Screens { get; init; } = [];

    [JsonConverter(typeof(LogLevelConverter))]
    public LogLevel LogLevel { get; set; } = LogLevel.Information;

    public string? NetworkConfig { get; init; }

    // optional — defaults to machine hostname without domain
    public string? Name { get; init; }

    [JsonIgnore]
    public string ResolvedName => Name ?? Environment.MachineName.Split('.')[0];

    [JsonIgnore]
    public ScreenConfig? LocalScreen => Screens.FirstOrDefault(s => s.Name == ResolvedName);

    [JsonIgnore]
    public IEnumerable<ScreenConfig> RemoteScreens => Screens.Where(s => s.Name != ResolvedName);

    public static HydraConfig Load(string path = "hydra.conf")
    {
        var json = File.ReadAllText(path);
        return json.FromSaneJson<HydraConfig>()
            ?? throw new InvalidOperationException("Failed to deserialize hydra.conf");
    }

    // maps SereneLogger short names (trce/dbug/info/warn/fail/crit) to LogLevel
    private sealed class LogLevelConverter : JsonConverter<LogLevel>
    {
        public override LogLevel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString()?.ToLowerInvariant() switch
            {
                "trce" or "trace" => LogLevel.Trace,
                "dbug" or "debug" => LogLevel.Debug,
                "info" or "information" => LogLevel.Information,
                "warn" or "warning" => LogLevel.Warning,
                "fail" or "error" => LogLevel.Error,
                "crit" or "critical" => LogLevel.Critical,
                var s => throw new JsonException($"Unknown log level: '{s}'")
            };

        public override void Write(Utf8JsonWriter writer, LogLevel value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value switch
            {
                LogLevel.Trace => "trce",
                LogLevel.Debug => "dbug",
                LogLevel.Information => "info",
                LogLevel.Warning => "warn",
                LogLevel.Error => "fail",
                LogLevel.Critical => "crit",
                _ => value.ToString()
            });
    }
}
