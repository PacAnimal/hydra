using System.Text.Json;
using System.Text.Json.Serialization;
using Cathedral.Config;
using Hydra.Screen;
using Microsoft.Extensions.Logging;

namespace Hydra.Config;

public class HydraConfig
{
    public required Mode Mode { get; set; }
    public List<ScreenRect> Screens { get; set; } = [];

    [JsonConverter(typeof(LogLevelConverter))]
    public LogLevel LogLevel { get; set; } = LogLevel.Information;

    public string? NetworkConfig { get; set; }
    public string? HostName { get; set; }

    public static HydraConfig Load(string path = "hydra.conf")
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<HydraConfig>(json, SaneJson.Options)
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
