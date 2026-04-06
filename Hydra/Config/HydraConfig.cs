using System.Text.Json;
using System.Text.Json.Serialization;
using Cathedral.Extensions;
using Hydra.Platform;
using Hydra.Screen;
using Microsoft.Extensions.Configuration;
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

    public bool AutoUpdate { get; init; } = true;
    public bool SyncScreensaver { get; init; } = true;

    // optional — defaults to machine hostname without domain
    public string? Name { get; init; }

    // optional — if set, this config only activates when the specified network condition is met
    public ConfigCondition? Condition { get; init; }
    // required when Condition == Ssid — the WiFi network name to match
    public string? Ssid { get; init; }

    [JsonIgnore]
    public string ResolvedName => Name ?? Environment.MachineName.Split('.')[0];

    [JsonIgnore]
    public HostConfig? LocalHost => Hosts.FirstOrDefault(s => s.Name.EqualsIgnoreCase(ResolvedName));

    [JsonIgnore]
    public IEnumerable<HostConfig> RemoteHosts => Hosts.Where(s => !s.Name.EqualsIgnoreCase(ResolvedName));

    // convenience method for single-config scenarios (tests, simple setups)
    // throws if the file contains multiple configs — use LoadAll() in that case
    public static HydraConfig Load(IConfiguration config)
    {
        var (configs, _) = LoadAll(config);
        if (configs.Count != 1)
            throw new InvalidOperationException("Config file contains multiple configs. Use HydraConfig.LoadAll() instead.");
        return configs[0];
    }

    // loads all configs from file, validates, and returns the list
    public static (List<HydraConfig> configs, string path) LoadAll(IConfiguration config)
    {
        var binaryDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var path = config.GetStringOrNull("CONFIG")
            ?? FindConfig(Path.Combine(binaryDir, "hydra.conf"))
            ?? FindConfig(Path.Combine(Directory.GetCurrentDirectory(), "hydra.conf"))
            ?? throw new FileNotFoundException("No hydra.conf found. Set CONFIG=/path/to/hydra.conf and try again.");

        var json = File.ReadAllText(path);
        var configs = ParseConfigs(json, path);
        Validate(configs);
        return (configs, path);
    }

    // resolves the active config from the list based on current network state.
    // returns null if no config matches (hydra should idle until network changes)
    public static HydraConfig? Resolve(List<HydraConfig> configs, List<NetworkState> active)
    {
        HydraConfig? fallback = null;

        foreach (var cfg in configs)
        {
            if (cfg.Condition == null)
            {
                fallback = cfg;
                continue;
            }

            if (cfg.Condition == ConfigCondition.Wired && active.Any(n => n.Type == ConfigCondition.Wired))
                return cfg;

            if (cfg.Condition == ConfigCondition.Ssid && active.Any(n => n.Type == ConfigCondition.Ssid &&
                    (n.Ssid?.EqualsIgnoreCase(cfg.Ssid) ?? false)))
                return cfg;
        }

        return fallback;
    }

    private static List<HydraConfig> ParseConfigs(string json, string path)
    {
        // try array first, fall back to single object
        try
        {
            var list = json.FromSaneJson<List<HydraConfig>>();
            if (list is { Count: > 0 })
                return list;
        }
        catch (JsonException) { }

        var single = json.FromSaneJson<HydraConfig>()
            ?? throw new InvalidOperationException($"Failed to deserialize {path}");
        return [single];
    }

    private static void Validate(List<HydraConfig> configs)
    {
        var defaults = configs.Count(c => c.Condition == null);
        if (defaults > 1)
            throw new InvalidOperationException("hydra.conf has multiple default configs (configs without a 'condition' field). Only one is allowed.");

        var ssids = configs
            .Where(c => c.Condition == ConfigCondition.Ssid)
            .Select(c => c.Ssid?.ToLowerInvariant())
            .ToList();
        var duplicateSsid = ssids.GroupBy(s => s).FirstOrDefault(g => g.Count() > 1);
        if (duplicateSsid != null)
            throw new InvalidOperationException($"hydra.conf has duplicate SSID config for '{duplicateSsid.Key}'.");

        var wiredCount = configs.Count(c => c.Condition == ConfigCondition.Wired);
        if (wiredCount > 1)
            throw new InvalidOperationException("hydra.conf has multiple Wired configs. Only one is allowed.");

        foreach (var cfg in configs.Where(c => c.Condition == ConfigCondition.Ssid))
        {
            if (string.IsNullOrWhiteSpace(cfg.Ssid))
                throw new InvalidOperationException("A config with 'condition: Ssid' must have a non-empty 'ssid' field.");
        }
    }

    private static string? FindConfig(string path) => File.Exists(path) ? path : null;

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
