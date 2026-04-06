using Cathedral.Extensions;
using Hydra.Config;
using Microsoft.Extensions.Logging;

namespace Hydra.Screen;

public class ScreenLayout(List<ScreenRect> screens, List<HostConfig> configs, int? defaultDeadCorners, ILogger log)
{
    private const int JumpZone = 1;
    private const int NudgeDistance = 2;

    private readonly Dictionary<(string Name, Direction Dir), List<EdgeLink>> _graph = BuildGraph(screens, configs, log);
    private readonly Dictionary<string, float> _deadCorners = BuildDeadCorners(configs, defaultDeadCorners);

    private static Dictionary<(string, Direction), List<EdgeLink>> BuildGraph(
        List<ScreenRect> screens, List<HostConfig> configs, ILogger log)
    {
        // group screens by host name for fast lookup
        var byHost = screens.ToLookup(s => s.Host, StringComparer.OrdinalIgnoreCase);
        var graph = new Dictionary<(string, Direction), List<EdgeLink>>();

        foreach (var config in configs)
        {
            var sourceScreens = byHost[config.Name].ToList();
            if (sourceScreens.Count == 0) continue;

            foreach (var neighbour in config.Neighbours)
            {
                var destScreens = byHost[neighbour.Name].ToList();

                // filter source screens by SourceScreen identifier if set
                var filteredSources = neighbour.SourceScreen is null
                    ? sourceScreens
                    : [.. sourceScreens.Where(s => MatchesId(s, neighbour.SourceScreen))];

                // filter dest screens by DestScreen identifier if set
                var filteredDests = neighbour.DestScreen is null
                    ? destScreens
                    : [.. destScreens.Where(s => MatchesId(s, neighbour.DestScreen))];

                if (neighbour.SourceScreen != null && filteredSources.Count > 1)
                {
                    log.LogWarning("Neighbour sourceScreen '{Id}' matches multiple screens ({Names}) — ignoring neighbour",
                        neighbour.SourceScreen, string.Join(", ", filteredSources.Select(s => s.Name)));
                    continue;
                }

                if (neighbour.DestScreen != null && filteredDests.Count > 1)
                {
                    log.LogWarning("Neighbour destScreen '{Id}' matches multiple screens ({Names}) — ignoring neighbour",
                        neighbour.DestScreen, string.Join(", ", filteredDests.Select(s => s.Name)));
                    continue;
                }

                var dest = filteredDests.FirstOrDefault();
                if (dest is null) continue;

                var srcStart = Math.Clamp(neighbour.SourceStart, 0, 100) / 100f;
                var srcEnd = Math.Clamp(neighbour.SourceEnd, 0, 100) / 100f;
                var dstStart = Math.Clamp(neighbour.DestStart, 0, 100) / 100f;
                var dstEnd = Math.Clamp(neighbour.DestEnd, 0, 100) / 100f;

                foreach (var source in filteredSources)
                {
                    var key = (source.Name, neighbour.Direction);
                    if (!graph.TryGetValue(key, out var links))
                        graph[key] = links = [];
                    links.Add(new EdgeLink(srcStart, srcEnd, dstStart, dstEnd, dest));
                }
            }
        }

        return graph;
    }

    private static Dictionary<string, float> BuildDeadCorners(List<HostConfig> configs, int? defaultDeadCorners)
    {
        var result = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var config in configs)
        {
            var raw = config.DeadCorners ?? defaultDeadCorners ?? 0;
            result[config.Name] = Math.Clamp(raw, 0, 100) / 100f;
        }
        return result;
    }

    private static bool MatchesId(ScreenRect screen, string id) =>
        screen.Identity?.Matches(id) ?? screen.Name.EqualsIgnoreCase(id);

    // checks if the cursor is in the jump zone of any edge that has a neighbour.
    // coords are 0-based within the current screen.
    // returns the neighbour and mapped entry coords, or null.
    public EdgeHit? DetectEdgeExit(ScreenRect current, int x, int y)
    {
        Direction? dir = null;
        int perpPos = 0; // position along the crossed edge (height for left/right, width for top/bottom)
        int edgeLen = 0;

        if (x <= JumpZone - 1)
        { dir = Direction.Left; perpPos = y; edgeLen = current.Height; }
        else if (x >= current.Width - JumpZone)
        { dir = Direction.Right; perpPos = y; edgeLen = current.Height; }
        else if (y <= JumpZone - 1)
        { dir = Direction.Top; perpPos = x; edgeLen = current.Width; }
        else if (y >= current.Height - JumpZone)
        { dir = Direction.Bottom; perpPos = x; edgeLen = current.Width; }

        if (dir is null) return null;
        if (!_graph.TryGetValue((current.Name, dir.Value), out var links)) return null;

        // normalized position along the edge [0,1]
        var normalized = edgeLen > 0 ? (perpPos + 0.5f) / edgeLen : 0f;

        // dead corners: block outbound transitions near either end of the edge
        if (_deadCorners.TryGetValue(current.Host, out var hc) && hc > 0f)
        {
            if (normalized < hc || normalized > 1f - hc)
                return null;
        }

        // find the link whose source range contains this position
        EdgeLink? match = null;
        foreach (var link in links)
        {
            if (normalized >= link.SourceStart && normalized <= link.SourceEnd)
            {
                match = link;
                break;
            }
        }
        if (match is null) return null;

        // skip-through offline screens (Width=0 means slave not yet connected)
        var dest = match.Destination;
        const int maxHops = 10;
        for (var hops = 0; dest.Width == 0 && hops < maxHops; hops++)
        {
            if (!_graph.TryGetValue((dest.Name, dir.Value), out var nextLinks) || nextLinks.Count == 0)
                return null;
            dest = nextLinks[0].Destination;
        }
        if (dest.Width == 0) return null;

        var entry = MapEntry(dest, dir.Value, perpPos, edgeLen, match);
        return new EdgeHit(dest, dir.Value, entry.X, entry.Y);
    }

    // maps cursor position from source edge range to dest entry coords with nudge
    private static EntryPoint MapEntry(
        ScreenRect to, Direction dir, int perpPos, int srcEdgeLen, EdgeLink link)
    {
        int destEdgeLen = dir is Direction.Left or Direction.Right ? to.Height : to.Width;

        // normalize within source range then project into dest range
        var srcSpan = (link.SourceEnd - link.SourceStart) * srcEdgeLen;
        var normalized = srcSpan > 0
            ? Math.Clamp(((perpPos + 0.5f) - link.SourceStart * srcEdgeLen) / srcSpan, 0f, 1f)
            : 0f;

        var destPos = Math.Clamp((int)(link.DestStart * destEdgeLen + normalized * (link.DestEnd - link.DestStart) * destEdgeLen), 0, destEdgeLen - 1);

        return dir switch
        {
            Direction.Right => new(NudgeDistance, destPos),
            Direction.Left => new(to.Width - 1 - NudgeDistance, destPos),
            Direction.Bottom => new(destPos, NudgeDistance),
            Direction.Top => new(destPos, to.Height - 1 - NudgeDistance),
            _ => throw new ArgumentOutOfRangeException(nameof(dir), dir, null),
        };
    }

    private record EntryPoint(int X, int Y);

    // one entry per edge segment connection
    private record EdgeLink(
        float SourceStart, float SourceEnd,   // normalized [0,1] range on source edge
        float DestStart, float DestEnd,       // normalized [0,1] range on dest edge
        ScreenRect Destination);
}
