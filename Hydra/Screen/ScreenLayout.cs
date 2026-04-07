using Cathedral.Extensions;
using Hydra.Config;
using Microsoft.Extensions.Logging;

namespace Hydra.Screen;

public class ScreenLayout(List<ScreenRect> screens, List<HostConfig> configs, int? defaultDeadCorners, Dictionary<string, decimal> screenScales, ILogger log)
{
    private const int JumpZone = 1;
    private const int NudgeDistance = 2;

    private readonly Dictionary<(string Name, Direction Dir), List<EdgeLink>> _graph = BuildGraph(screens, configs, log);
    private readonly Dictionary<string, int> _deadCorners = BuildDeadCorners(screens, configs, defaultDeadCorners, screenScales);

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

                // when no sourceScreen is specified and there are multiple screens,
                // only apply to screens on the physical outer edge for this direction
                if (neighbour.SourceScreen is null && filteredSources.Count > 1)
                    filteredSources = FilterToEdgeScreens(filteredSources, neighbour.Direction);

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

    private static Dictionary<string, int> BuildDeadCorners(
        List<ScreenRect> screens, List<HostConfig> configs, int? defaultDeadCorners, Dictionary<string, decimal> screenScales)
    {
        var byHost = configs.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var screen in screens)
        {
            var pixels = (byHost.TryGetValue(screen.Host, out var cfg) ? cfg.DeadCorners : null) ?? defaultDeadCorners ?? 0;
            if (pixels <= 0) continue;
            var scale = screenScales.GetValueOrDefault(screen.Name, 1.0m);
            result[screen.Name] = (int)Math.Round(pixels * (double)scale);
        }
        return result;
    }

    private static bool MatchesId(ScreenRect screen, string id) =>
        screen.Identity?.Matches(id) ?? screen.Name.EqualsIgnoreCase(id);

    private static List<ScreenRect> FilterToEdgeScreens(List<ScreenRect> screens, Direction dir)
    {
        switch (dir)
        {
            case Direction.Right: { var v = screens.Max(s => s.X + s.Width); return [.. screens.Where(s => s.X + s.Width == v)]; }
            case Direction.Left: { var v = screens.Min(s => s.X); return [.. screens.Where(s => s.X == v)]; }
            case Direction.Down: { var v = screens.Max(s => s.Y + s.Height); return [.. screens.Where(s => s.Y + s.Height == v)]; }
            case Direction.Up: { var v = screens.Min(s => s.Y); return [.. screens.Where(s => s.Y == v)]; }
            default: throw new ArgumentOutOfRangeException(nameof(dir), dir, null);
        }
    }

    // checks if the cursor is in the jump zone of any edge that has a neighbour.
    // coords are 0-based within the current screen.
    // returns the neighbour and mapped entry coords, or null.
    public EdgeHit? DetectEdgeExit(ScreenRect current, int x, int y)
    {
        Direction? dir = null;
        int perpPos = 0; // position along the crossed edge (height for left/right, width for up/down)
        int edgeLen = 0;

        if (x <= JumpZone - 1)
        { dir = Direction.Left; perpPos = y; edgeLen = current.Height; }
        else if (x >= current.Width - JumpZone)
        { dir = Direction.Right; perpPos = y; edgeLen = current.Height; }
        else if (y <= JumpZone - 1)
        { dir = Direction.Up; perpPos = x; edgeLen = current.Width; }
        else if (y >= current.Height - JumpZone)
        { dir = Direction.Down; perpPos = x; edgeLen = current.Width; }

        if (dir is null) return null;
        if (!_graph.TryGetValue((current.Name, dir.Value), out var links)) return null;

        // normalized position along the edge [0,1]
        var normalized = edgeLen > 0 ? (perpPos + 0.5f) / edgeLen : 0f;

        // dead corners: block outbound transitions near either end of the edge
        if (_deadCorners.TryGetValue(current.Name, out var dc) && (perpPos < dc || perpPos >= edgeLen - dc))
            return null;

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
            Direction.Down => new(destPos, NudgeDistance),
            Direction.Up => new(destPos, to.Height - 1 - NudgeDistance),
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
