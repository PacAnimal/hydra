using Cathedral.Extensions;
using Hydra.Relay;

namespace Hydra.Screen;

public static class ScreenNaming
{
    public static string BuildScreenName(string hostname, int index, int total) =>
        total == 1 ? hostname : $"{hostname}:{index}";

    // true if name is host or host:<ascii digits>, ignoring case
    public static bool Owns(string host, string name)
    {
        if (name.EqualsIgnoreCase(host)) return true;
        if (name.Length <= host.Length + 1 || !name.StartsWithIgnoreCase(host) || name[host.Length] != ':') return false;
        var index = name.AsSpan(host.Length + 1);
        return !index.ContainsAnyExceptInRange('0', '9');
    }

    // the entries host may advertise: its own names, each once (the first wins)
    public static OwnedScreens OwnedBy(string host, IEnumerable<ScreenInfoEntry> entries)
    {
        var kept = new List<ScreenInfoEntry>();
        var refused = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            if (Owns(host, e.Name) && seen.Add(e.Name)) kept.Add(e);
            else refused.Add(e.Name);
        }
        return new OwnedScreens(kept, refused);
    }

    public sealed record OwnedScreens(List<ScreenInfoEntry> Kept, List<string> Refused);
}
