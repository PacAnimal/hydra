using Cathedral.Extensions;

namespace Hydra.Config;

public class ConfigConditions
{
    public string? Ssid { get; init; }
    public int? ScreenCount { get; init; }
    public bool? IsPluggedIn { get; init; }

    internal bool IsEmpty => Ssid == null && ScreenCount == null && IsPluggedIn == null;

    // every condition set here holds in state (AND logic)
    internal bool Matches(ConditionState state, bool ignoreScreenCount = false)
    {
        if (Ssid != null && !state.ActiveSsids.Any(s => s.EqualsIgnoreCase(Ssid))) return false;
        if (!ignoreScreenCount && ScreenCount != null && state.ScreenCount != ScreenCount) return false;
        return IsPluggedIn == null || state.IsPluggedIn == IsPluggedIn;
    }
}
