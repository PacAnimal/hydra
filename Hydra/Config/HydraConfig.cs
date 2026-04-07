using System.Text.Json;
using System.Text.Json.Serialization;
using Cathedral.Extensions;
using Hydra.Screen;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Hydra.Config;

public class HostConfig
{
    public required string Name { get; init; }
    public List<NeighbourConfig> Neighbours { get; init; } = [];
    public int? DeadCorners { get; init; }  // pixel dead zone at screen corners; overrides root-level setting
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
    public bool Mirror { get; init; } = true;         // auto-create the reverse mapping
}

public class ScreenDefinition
{
    public string? DisplayName { get; init; }  // matches DetectedScreen.DisplayName (e.g. "DELL U2720Q")
    public string? OutputName { get; init; }   // matches DetectedScreen.OutputName (e.g. "HDMI-1")
    public string? PlatformId { get; init; }   // matches DetectedScreen.PlatformId
    public decimal? MouseScale { get; init; }  // cursor speed multiplier for this screen; overrides root mouseScale
}

public class HydraConfig
{
    public required Mode Mode { get; init; }
    // master only — ignored in slave mode
    public List<HostConfig> Hosts { get; init; } = [];
    // per-screen scale config — used by both master and slave
    public List<ScreenDefinition> ScreenDefinitions { get; init; } = [];
    public decimal? MouseScale { get; init; }  // fallback cursor speed multiplier; overridden by per-screen mouseScale

    [JsonConverter(typeof(LogLevelConverter))]
    public LogLevel LogLevel { get; set; } = LogLevel.Information;

    public string? NetworkConfig { get; init; }

    public bool AutoUpdate { get; init; } = true;
    public bool SyncScreensaver { get; init; } = true;
    public bool DebugShield { get; init; } = false;
    public int? DeadCorners { get; init; }  // pixel dead zone at screen corners; scaled by screen scale; per-host setting overrides this

    // optional — defaults to machine hostname without domain
    public string? Name { get; init; }

    // optional — if set, hydra refuses to start if another instance holds the lock on this file
    public string? LockFile { get; init; }

    // optional — if set, this config only activates when all specified conditions are met
    public ConfigConditions? Conditions { get; init; }

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
        configs.ForEach(c => ExpandMirrors(c.Hosts));
        Validate(configs);
        return (configs, path);
    }

    // true if any config has conditions — if false, the single unconditional config is always active
    public static bool HasConditions(List<HydraConfig> configs) => configs.Any(c => c.Conditions != null);

    // true if any config matches on SSID — determines whether WiFi detection is needed
    public static bool HasSsidConditions(List<HydraConfig> configs) => configs.Any(c => c.Conditions?.Ssid != null);

    // resolves the active config from the list based on currently active SSIDs.
    // returns null if no config matches (hydra should idle until network changes)
    public static HydraConfig? Resolve(List<HydraConfig> configs, List<string> activeSsids)
    {
        HydraConfig? fallback = null;

        foreach (var cfg in configs)
        {
            if (cfg.Conditions == null)
            {
                fallback = cfg;
                continue;
            }

            if (cfg.Conditions.Ssid != null && activeSsids.Any(s => s.EqualsIgnoreCase(cfg.Conditions.Ssid)))
                return cfg;
        }

        return fallback;
    }

    // parses and validates a JSON string — used in tests to exercise validation logic directly
    internal static List<HydraConfig> ParseAndValidate(string json)
    {
        var configs = ParseConfigs(json, "<test>");
        configs.ForEach(c => ExpandMirrors(c.Hosts));
        Validate(configs);
        return configs;
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

    // expands mirror neighbours: for each neighbour with Mirror != false, auto-creates the reverse
    // mapping on the target host if one doesn't already exist. target hosts are created if missing.
    internal static void ExpandMirrors(List<HostConfig> hosts)
    {
        // snapshot to avoid iterating while mutating
        var snapshot = hosts.Select(h => (h.Name, Neighbours: h.Neighbours.ToList())).ToList();

        foreach (var (sourceName, neighbours) in snapshot)
        {
            foreach (var n in neighbours)
            {
                if (!n.Mirror) continue;

                var oppositeDir = n.Direction.Opposite();

                // find or create the target host
                var target = hosts.FirstOrDefault(h => h.Name.EqualsIgnoreCase(n.Name));
                if (target is null)
                {
                    target = new HostConfig { Name = n.Name };
                    hosts.Add(target);
                }

                // skip if target already has an explicit reverse mapping back to source
                if (target.Neighbours.Any(r => r.Direction == oppositeDir && r.Name.EqualsIgnoreCase(sourceName)))
                    continue;

                target.Neighbours.Add(new NeighbourConfig
                {
                    Direction = oppositeDir,
                    Name = sourceName,
                    SourceStart = n.DestStart,
                    SourceEnd = n.DestEnd,
                    DestStart = n.SourceStart,
                    DestEnd = n.SourceEnd,
                    SourceScreen = n.DestScreen,
                    DestScreen = n.SourceScreen,
                    Mirror = false,
                });
            }
        }
    }

    private static void Validate(List<HydraConfig> configs)
    {
        var defaults = configs.Count(c => c.Conditions == null);
        if (defaults > 1)
            throw new InvalidOperationException("hydra.conf has multiple default configs (configs without a 'conditions' field). Only one is allowed.");

        foreach (var cfg in configs.Where(c => c.Conditions != null))
        {
            if (cfg.Conditions!.Ssid == null)
                throw new InvalidOperationException("A config with 'conditions' must have at least one condition field set (e.g. 'ssid').");
        }

        var ssids = configs
            .Where(c => c.Conditions?.Ssid != null)
            .Select(c => c.Conditions!.Ssid!.ToLowerInvariant())
            .ToList();
        var duplicateSsid = ssids.GroupBy(s => s).FirstOrDefault(g => g.Count() > 1);
        if (duplicateSsid != null)
            throw new InvalidOperationException($"hydra.conf has duplicate SSID config for '{duplicateSsid.Key}'.");

        foreach (var cfg in configs)
        {
            foreach (var def in cfg.ScreenDefinitions)
            {
                if (def.DisplayName == null && def.OutputName == null && def.PlatformId == null)
                    throw new InvalidOperationException("A screenDefinition entry has no matching criteria (displayName, outputName, platformId are all null) — it can never match any screen.");
            }
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
