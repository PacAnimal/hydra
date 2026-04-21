using Cathedral.Extensions;
using Hydra.Screen;

namespace Hydra.Config;

public record ConditionState(List<string> ActiveSsids, int ScreenCount);

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
    public decimal? MouseScale { get; init; }          // cursor speed multiplier for this screen; overrides root mouseScale
    public decimal? RelativeMouseScale { get; init; }  // relative-mode speed multiplier; overrides root relativeMouseScale
}

public class HydraConfig
{
    public required Mode Mode { get; init; }
    public string? ProfileName { get; init; }  // displayed when logging which profile is active
    // master only — ignored in slave mode
    public List<HostConfig> Hosts { get; init; } = [];
    // slave only — scale config is reported to master via ScreenInfoEntry, master applies it when routing to slave screens
    public List<ScreenDefinition> ScreenDefinitions { get; init; } = [];
    public decimal? MouseScale { get; init; }          // slave only — fallback cursor speed multiplier; overridden by per-screen mouseScale
    public decimal? RelativeMouseScale { get; init; }  // slave only — fallback relative-mode cursor speed; overridden by per-screen relativeMouseScale

    public string? NetworkConfig { get; init; }

    public bool RemoteOnly { get; init; } = false;
    public bool SyncScreensaver { get; init; } = true;
    public int? DeadCorners { get; init; }  // pixel dead zone at screen corners; scaled by screen scale; per-host setting overrides this

    // optional — if set, this config only activates when all specified conditions are met
    public ConfigConditions? Conditions { get; init; }

    public HostConfig? LocalHost(string resolvedName) => Hosts.FirstOrDefault(s => s.Name.EqualsIgnoreCase(resolvedName));

    public IEnumerable<HostConfig> RemoteHosts(string resolvedName) => Hosts.Where(s => !s.Name.EqualsIgnoreCase(resolvedName));

    // true if any profile has effective conditions — if false, the single unconditional profile is always active
    public static bool HasConditions(List<HydraConfig> profiles) => profiles.Any(c => c.Conditions?.IsEmpty == false);

    // true if any profile matches on SSID — determines whether WiFi detection is needed
    public static bool HasSsidConditions(List<HydraConfig> profiles) => profiles.Any(c => c.Conditions?.Ssid != null);

    // true if any profile matches on screen count — determines whether screen count detection is needed
    public static bool HasScreenCountConditions(List<HydraConfig> profiles) => profiles.Any(c => c.Conditions?.ScreenCount != null);

    // resolves the active profile from the list based on current condition state.
    // if profileOverride is set, that profile is returned unconditionally (ignores conditions).
    // returns null if no profile matches (hydra should idle until conditions change)
    public static HydraConfig? Resolve(List<HydraConfig> profiles, ConditionState state, string? profileOverride = null)
    {
        if (profileOverride != null)
            return profiles.FirstOrDefault(c => c.ProfileName != null && c.ProfileName.EqualsIgnoreCase(profileOverride));

        HydraConfig? fallback = null;

        foreach (var cfg in profiles)
        {
            if (cfg.Conditions == null || cfg.Conditions.IsEmpty)
            {
                fallback = cfg;
                continue;
            }

            // all specified conditions must match (AND logic)
            if (cfg.Conditions.Ssid != null && !state.ActiveSsids.Any(s => s.EqualsIgnoreCase(cfg.Conditions.Ssid)))
                continue;
            if (cfg.Conditions.ScreenCount != null && state.ScreenCount != cfg.Conditions.ScreenCount)
                continue;

            return cfg;
        }

        return fallback;
    }

    // parses and validates a JSON string — used in tests to exercise validation logic directly
    internal static List<HydraConfig> ParseAndValidate(string json)
    {
        var file = HydraConfigFile.Parse(json, "<test>");
        return file.Profiles;
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

    internal static void Validate(List<HydraConfig> profiles, string resolvedName, string? profileOverride = null)
    {
        if (profileOverride != null)
        {
            var exists = profiles.Any(c => c.ProfileName != null && c.ProfileName.EqualsIgnoreCase(profileOverride));
            if (!exists)
                throw new InvalidOperationException($"hydra.conf 'profile' override '{profileOverride}' does not match any profile's profileName.");
        }

        // no duplicate profile names
        var names = profiles.Where(c => !string.IsNullOrWhiteSpace(c.ProfileName))
            .GroupBy(c => c.ProfileName!.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (names != null)
            throw new InvalidOperationException($"hydra.conf has duplicate profile name '{names.Key}'.");

        // empty conditions ({}) is treated as unconditional — count those as defaults too
        var defaults = profiles.Count(c => c.Conditions == null || c.Conditions.IsEmpty);
        if (defaults > 1)
            throw new InvalidOperationException("hydra.conf has multiple default profiles (profiles without a 'conditions' field). Only one is allowed.");

        foreach (var cfg in profiles.Where(c => c.RemoteOnly))
        {
            if (cfg.Mode != Mode.Master)
                throw new InvalidOperationException("remoteOnly requires mode: Master.");
            var hasRemoteHost = cfg.Hosts.Any(h => !h.Name.EqualsIgnoreCase(resolvedName));
            if (!hasRemoteHost)
                throw new InvalidOperationException("remoteOnly requires at least one remote host in the hosts list.");
        }

        foreach (var cfg in profiles.Where(c => c.Conditions?.IsEmpty == false))
        {
            if (cfg.Conditions!.ScreenCount is < 1)
                throw new InvalidOperationException("screenCount condition must be >= 1.");
        }

        // no two conditional profiles may have identical (ssid, screenCount) tuples
        var conditionKeys = profiles
            .Where(c => c.Conditions?.IsEmpty == false)
            .Select(c => (Ssid: c.Conditions!.Ssid?.ToLowerInvariant(), c.Conditions.ScreenCount))
            .ToList();
        var duplicate = conditionKeys.GroupBy(k => k).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
            throw new InvalidOperationException($"hydra.conf has duplicate conditions for ssid='{duplicate.Key.Ssid}' screenCount='{duplicate.Key.ScreenCount}'.");

        foreach (var cfg in profiles)
        {
            // no duplicate host names within a profile
            var dupHost = cfg.Hosts
                .GroupBy(h => h.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(g => g.Count() > 1);
            if (dupHost != null)
                throw new InvalidOperationException($"hydra.conf has duplicate host name '{dupHost.Key}' in profile '{cfg.ProfileName ?? "(default)"}'.");

            if (cfg.Mode == Mode.Master && cfg.MouseScale != null)
                throw new InvalidOperationException("mouseScale is slave-only. Remove it from master profiles.");
            if (cfg.Mode == Mode.Master && cfg.ScreenDefinitions.Count > 0)
                throw new InvalidOperationException("screenDefinitions is slave-only. Remove it from master profiles.");

            foreach (var def in cfg.ScreenDefinitions)
            {
                if (def.DisplayName == null && def.OutputName == null && def.PlatformId == null)
                    throw new InvalidOperationException("A screenDefinition entry has no matching criteria (displayName, outputName, platformId are all null) — it can never match any screen.");
            }
        }
    }
}
