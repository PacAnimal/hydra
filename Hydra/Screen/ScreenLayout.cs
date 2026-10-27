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

                // likewise land on a screen on the physical outer edge we arrive through, so a mirrored link
                // returns to the screen its forward link leaves from, not whichever one enumerated first
                if (neighbour.DestScreen is null && filteredDests.Count > 1)
                    filteredDests = FilterToEdgeScreens(filteredDests, neighbour.Direction.Opposite());

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
    // returns the neighbour, the mapped entry coords and the edge left by, or null.
    public EdgeHit? DetectEdgeExit(ScreenRect current, int x, int y)
    {
        Direction? dir = null;
        if (x <= JumpZone - 1) dir = Direction.Left;
        else if (x >= current.Width - JumpZone) dir = Direction.Right;
        else if (y <= JumpZone - 1) dir = Direction.Up;
        else if (y >= current.Height - JumpZone) dir = Direction.Down;

        if (dir is null) return null;
        var perpPos = dir is Direction.Left or Direction.Right ? y : x; // position along the crossed edge
        var edgeLen = EdgeLength(current, dir.Value);
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

        if (Online(match.Destination, dir.Value) is not { } dest) return null;

        var entry = MapEntry(dest, dir.Value, perpPos, edgeLen, match);
        return new EdgeHit(dest, dir.Value, entry, new ScreenEdge(current.Name, dir.Value, perpPos));
    }

    // skips through offline screens (Width/Height == 0 means slave not yet connected) to the next one on in dir
    private ScreenRect? Online(ScreenRect dest, Direction dir)
    {
        const int maxHops = 10;
        for (var hops = 0; IsOffline(dest) && hops < maxHops; hops++)
        {
            if (!_graph.TryGetValue((dest.Name, dir), out var nextLinks) || nextLinks.Count == 0)
                return null;
            dest = nextLinks[0].Destination;
        }
        return IsOffline(dest) ? null : dest;
    }

    private static bool IsOffline(ScreenRect screen) => screen.Width == 0 || screen.Height == 0;

    // The screen the monitor switch hotkey goes to from `from` through its `edge`: the one on that edge, or of several,
    // the closest to the aimed point along it (its middle, or the middle of the end third a perpendicular `third`
    // names), a tie going to the left or upper one. Same-host neighbours are found by touching `from`, other hosts' by
    // the configured links. Null when nothing is there, or `third` is not perpendicular to `edge`.
    public ScreenRect? SwitchTarget(ScreenRect from, Direction edge, Direction? third, bool includeLocal)
    {
        if (third is { } t && !t.IsPerpendicularTo(edge)) return null;
        var aim = third switch
        {
            Direction.Left or Direction.Up => 1 / 6.0,
            Direction.Right or Direction.Down => 5 / 6.0,
            _ => 0.5,
        };
        var candidates = Neighbours(from, edge).Where(c => includeLocal || !c.Screen.IsLocal).ToList();
        return candidates.Count switch
        {
            0 => null,
            1 => candidates[0].Screen,
            _ => candidates.OrderBy(c => Distance(aim, c.Start, c.End)).ThenBy(c => c.Start).First().Screen,
        };
    }

    private static double Distance(double aim, double start, double end) => aim < start ? start - aim : aim > end ? aim - end : 0;

    // every screen across `edge` of `from`, with the part of that edge (0 to 1) it lies along; one entry per screen
    private List<EdgeNeighbour> Neighbours(ScreenRect from, Direction edge)
    {
        var found = new List<EdgeNeighbour>();
        foreach (var s in screens)
        {
            if (s == from || IsOffline(s) || !s.Host.EqualsIgnoreCase(from.Host) || !Touches(from, s, edge)) continue;
            var (start, end, length, origin) = edge.IsVertical()
                ? (Math.Max(from.X, s.X), Math.Min(from.X + from.Width, s.X + s.Width), from.Width, from.X)
                : (Math.Max(from.Y, s.Y), Math.Min(from.Y + from.Height, s.Y + s.Height), from.Height, from.Y);
            if (end > start) found.Add(new EdgeNeighbour(s, (start - origin) / (double)length, (end - origin) / (double)length));
        }
        if (_graph.TryGetValue((from.Name, edge), out var links))
        {
            foreach (var link in links)
            {
                if (Online(link.Destination, edge) is not { } dest) continue;
                var at = found.FindIndex(n => n.Screen == dest);
                if (at < 0) found.Add(new EdgeNeighbour(dest, link.SourceStart, link.SourceEnd));
                else found[at] = found[at] with { Start = Math.Min(found[at].Start, link.SourceStart), End = Math.Max(found[at].End, link.SourceEnd) };
            }
        }
        return found;
    }

    private static bool Touches(ScreenRect from, ScreenRect s, Direction edge) => edge switch
    {
        Direction.Up => s.Y + s.Height == from.Y,
        Direction.Down => s.Y == from.Y + from.Height,
        Direction.Left => s.X + s.Width == from.X,
        Direction.Right => s.X == from.X + from.Width,
        _ => throw new ArgumentOutOfRangeException(nameof(edge), edge, null),
    };

    // maps cursor position from source edge range to dest entry coords with nudge
    private static ScreenPoint MapEntry(
        ScreenRect to, Direction dir, int perpPos, int srcEdgeLen, EdgeLink link)
    {
        var destEdgeLen = EdgeLength(to, dir);

        // normalize within source range then project into dest range
        var srcSpan = (link.SourceEnd - link.SourceStart) * srcEdgeLen;
        var normalized = srcSpan > 0
            ? Math.Clamp(((perpPos + 0.5f) - link.SourceStart * srcEdgeLen) / srcSpan, 0f, 1f)
            : 0f;

        var destPos = Math.Clamp((int)(link.DestStart * destEdgeLen + normalized * (link.DestEnd - link.DestStart) * destEdgeLen), 0, destEdgeLen - 1);
        return InsetFromEdge(to, dir, destPos);
    }

    // Where a return with no edge of its own comes back through the edge a visit left by: just inside it on that screen
    // as it is now, so a resized screen's new edge, or null when the screen is gone or empty.
    public static ScreenPoint? LandingBack(ScreenEdge exit, IEnumerable<ScreenRect> screens)
    {
        var screen = screens.FirstOrDefault(s => s.Name.EqualsIgnoreCase(exit.Screen));
        if (screen is null) return null;
        var edgeLen = EdgeLength(screen, exit.Edge);
        if (edgeLen <= 0) return null;
        return screen.ToGlobal(InsetFromEdge(screen, exit.Edge.Opposite(), Math.Clamp(exit.Position, 0, edgeLen - 1)));
    }

    // a point just inside the edge a cursor moving in arrivingDir enters `screen` by, edgePos along it (screen-local);
    // the nudge clears the jump zone so the landing never re-crosses
    private static ScreenPoint InsetFromEdge(ScreenRect screen, Direction arrivingDir, int edgePos) => arrivingDir switch
    {
        Direction.Right => new(NudgeDistance, edgePos),
        Direction.Left => new(screen.Width - 1 - NudgeDistance, edgePos),
        Direction.Down => new(edgePos, NudgeDistance),
        Direction.Up => new(edgePos, screen.Height - 1 - NudgeDistance),
        _ => throw new ArgumentOutOfRangeException(nameof(arrivingDir), arrivingDir, null),
    };

    // length of the edge a cursor crosses moving in dir
    private static int EdgeLength(ScreenRect screen, Direction dir) => dir is Direction.Left or Direction.Right ? screen.Height : screen.Width;

    private sealed record EdgeNeighbour(ScreenRect Screen, double Start, double End);

    // one entry per edge segment connection
    private record EdgeLink(
        float SourceStart, float SourceEnd,   // normalized [0,1] range on source edge
        float DestStart, float DestEnd,       // normalized [0,1] range on dest edge
        ScreenRect Destination);
}
