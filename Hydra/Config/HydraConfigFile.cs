using System.Text.Json;
using System.Text.Json.Serialization;
using Cathedral.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Hydra.Config;

public class HydraConfigFile
{
    public bool AutoUpdate { get; init; } = true;

    [JsonConverter(typeof(LogLevelConverter))]
    public LogLevel LogLevel { get; init; } = LogLevel.Information;

    // optional — if set, hydra refuses to start if another instance holds the lock on this file
    public string? LockFile { get; init; }

    // optional — if set, log output is also written to this file
    public string? LogFile { get; init; }

    // optional — defaults to machine hostname without domain
    public string? Name { get; init; }

    public List<HydraConfig> Profiles { get; init; } = [];

    // convenience method for single-profile scenarios (tests, simple setups)
    public static HydraConfigFile Load(IConfiguration config)
    {
        var (file, _) = LoadAll(config);
        return file;
    }

    public static (HydraConfigFile file, string path) LoadAll(IConfiguration config)
    {
        var binaryDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var path = config.GetStringOrNull("CONFIG")
            ?? FindConfig(Path.Combine(binaryDir, "hydra.conf"))
            ?? FindConfig(Path.Combine(Directory.GetCurrentDirectory(), "hydra.conf"))
            ?? throw new FileNotFoundException("No hydra.conf found. Set CONFIG=/path/to/hydra.conf and try again.");

        var json = File.ReadAllText(path);
        var file = Parse(json, path);
        return (file, path);
    }

    internal static HydraConfigFile Parse(string json, string path)
    {
        var file = json.FromSaneJson<HydraConfigFile>()
            ?? throw new InvalidOperationException($"Failed to deserialize {path}");
        file.Profiles.ForEach(p => HydraConfig.ExpandMirrors(p.Hosts));
        HydraConfig.Validate(file.Profiles, file.Name ?? Environment.MachineName.Split('.')[0]);
        return file;
    }

    private static string? FindConfig(string path) => File.Exists(path) ? path : null;
}

// maps SereneLogger short names (trce/dbug/info/warn/fail/crit) to LogLevel
internal sealed class LogLevelConverter : JsonConverter<LogLevel>
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
