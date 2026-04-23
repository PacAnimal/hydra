using Cathedral.Extensions;
using Microsoft.Extensions.Logging;

namespace Hydra.Config;

public interface IHydraProfile
{
    // root-level settings (constant for the life of this process)
    string Name { get; }
    LogLevel LogLevel { get; }
    bool AutoUpdate { get; }
    bool DebugShield { get; }

    // active profile settings
    string? ProfileName { get; }
    Mode Mode { get; }
    List<HostConfig> Hosts { get; }
    List<ScreenDefinition> ScreenDefinitions { get; }
    decimal? MouseScale { get; }
    decimal? RelativeMouseScale { get; }
    string? NetworkConfig { get; }
    bool RemoteOnly { get; }
    bool SyncScreensaver { get; }
    int? DeadCorners { get; }

    // computed from Name + Hosts
    HostConfig? LocalHost { get; }
    IEnumerable<HostConfig> RemoteHosts { get; }
}

public class HydraProfile(HydraConfigFile configFile, HydraConfig? activeProfile, string? networkConfigOverride = null) : IHydraProfile
{
    private readonly HydraConfig? _activeProfile = activeProfile;

    public string Name { get; } = configFile.Name ?? Environment.MachineName.Split('.')[0];
    public LogLevel LogLevel { get; } = configFile.LogLevel;
    public bool AutoUpdate { get; } = configFile.AutoUpdate;
    public bool DebugShield { get; } = configFile.DebugShield;

    public string? ProfileName => _activeProfile?.ProfileName;
    public Mode Mode => _activeProfile?.Mode ?? Mode.Slave;
    public List<HostConfig> Hosts => _activeProfile?.Hosts ?? [];
    public List<ScreenDefinition> ScreenDefinitions => _activeProfile?.ScreenDefinitions ?? [];
    public decimal? MouseScale => _activeProfile?.MouseScale;
    public decimal? RelativeMouseScale => _activeProfile?.RelativeMouseScale;
    public string? NetworkConfig => networkConfigOverride ?? _activeProfile?.NetworkConfig;
    public bool RemoteOnly => _activeProfile?.RemoteOnly ?? false;
    public bool SyncScreensaver => _activeProfile?.SyncScreensaver ?? true;
    public int? DeadCorners => _activeProfile?.DeadCorners;

    public HostConfig? LocalHost => Hosts.FirstOrDefault(h => h.Name.EqualsIgnoreCase(Name));
    public IEnumerable<HostConfig> RemoteHosts => Hosts.Where(h => !h.Name.EqualsIgnoreCase(Name));
}
