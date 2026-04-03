using System.Text.Json;
using System.Text.Json.Serialization;
using Cathedral.Extensions;
using Hydra.Screen;
using Microsoft.Extensions.Logging;

namespace Hydra.Config;

public class HostConfig
{
    public required string Name { get; init; }
    public List<NeighbourConfig> Neighbours { get; init; } = [];
}

public class NeighbourConfig
{
    public required Direction Direction { get; init; }
    public required string Name { get; init; }       // target host
    public string? SourceScreen { get; init; }       // optional: restrict to this local screen identifier
    public string? DestScreen { get; init; }         // optional: target this specific remote screen identifier
    public int SourceStart { get; init; }             // % of source edge (0-100)
    public int SourceEnd { get; init; } = 100;        // % of source edge (0-100)
    public int DestStart { get; init; }               // % of dest edge (0-100)
    public int DestEnd { get; init; } = 100;          // % of dest edge (0-100)
}

public class ScreenDefinition
{
    public required string Match { get; init; }  // screen identifier: display name, output connector, or platform id
    public decimal Scale { get; init; } = 1.0m;  // cursor speed multiplier for this screen
}

public class HydraConfig
{
    public required Mode Mode { get; init; }
    // master only — ignored in slave mode
    public List<HostConfig> Hosts { get; init; } = [];
    // per-screen scale config — used by both master and slave
    public List<ScreenDefinition> ScreenDefinitions { get; init; } = [];

    [JsonConverter(typeof(LogLevelConverter))]
    public LogLevel LogLevel { get; set; } = LogLevel.Information;

    public string? NetworkConfig { get; init; }

    // optional — defaults to machine hostname without domain
    public string? Name { get; init; }

    [JsonIgnore]
    public string ResolvedName => Name ?? Environment.MachineName.Split('.')[0];

    [JsonIgnore]
    public HostConfig? LocalHost => Hosts.FirstOrDefault(s => s.Name.EqualsIgnoreCase(ResolvedName));

    [JsonIgnore]
    public IEnumerable<HostConfig> RemoteHosts => Hosts.Where(s => !s.Name.EqualsIgnoreCase(ResolvedName));

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
