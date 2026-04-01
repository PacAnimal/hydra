using Hydra.Config;

namespace Hydra.Screen;

public class ScreenLayout(List<ScreenRect> screens, List<ScreenConfig> configs)
{
    private const int JumpZone = 1;
    private const int NudgeDistance = 2;

    // (screenName, direction) → (destination, scale, offset)
    private readonly Dictionary<(string Name, Direction Dir), (ScreenRect Dest, decimal Scale, int Offset)> _graph = BuildGraph(screens, configs);
    private readonly List<ScreenRect> _screens = screens;

    private static Dictionary<(string, Direction), (ScreenRect, decimal, int)> BuildGraph(
        List<ScreenRect> screens, List<ScreenConfig> configs)
    {
        var byName = screens.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var graph = new Dictionary<(string, Direction), (ScreenRect, decimal, int)>();

        foreach (var config in configs)
        {
            foreach (var neighbour in config.Neighbours)
            {
                if (byName.TryGetValue(neighbour.Name, out var dest))
                    graph[(config.Name, neighbour.Direction)] = (dest, neighbour.Scale, neighbour.Offset);
            }
        }

        return graph;
    }

    // returns the screen containing point (x, y) in 0-based coords, or null
    public ScreenRect? GetScreenAt(int x, int y) =>
        _screens.FirstOrDefault(s => x >= 0 && x < s.Width && y >= 0 && y < s.Height);

    // checks if the cursor is in the jump zone of any edge that has a neighbour.
    // coords are 0-based within the current screen.
    // returns the neighbour and mapped entry coords, or null.
    public EdgeHit? DetectEdgeExit(ScreenRect current, int x, int y)
    {
        Direction? dir = null;

        if (x <= JumpZone - 1)
            dir = Direction.Left;
        else if (x >= current.Width - JumpZone)
            dir = Direction.Right;
        else if (y <= JumpZone - 1)
            dir = Direction.Top;
        else if (y >= current.Height - JumpZone)
            dir = Direction.Bottom;

        if (dir is null)
            return null;

        if (!_graph.TryGetValue((current.Name, dir.Value), out var link))
            return null;

        var (dest, scale, offset) = link;

        // skip-through offline screens (Width=0 means slave not yet connected)
        const int maxHops = 10;
        for (var hops = 0; dest.Width == 0 && hops < maxHops; hops++)
        {
            if (!_graph.TryGetValue((dest.Name, dir.Value), out link))
                return null;
            (dest, scale, offset) = link;
        }

        if (dest.Width == 0)
            return null;

        var (entryX, entryY) = MapEntry(current, dest, dir.Value, x, y, offset);
        return new EdgeHit(dest, dir.Value, entryX, entryY, scale);
    }

    // maps cursor position from source edge to entry coords on the destination, with offset and nudge
    private static (int entryX, int entryY) MapEntry(
        ScreenRect from, ScreenRect to, Direction dir, int x, int y, int offset)
    {
        return dir switch
        {
            Direction.Right => (NudgeDistance, ApplyOffset(MapFraction(y, from.Height, to.Height), to.Height, offset)),
            Direction.Left => (to.Width - 1 - NudgeDistance, ApplyOffset(MapFraction(y, from.Height, to.Height), to.Height, offset)),
            Direction.Bottom => (ApplyOffset(MapFraction(x, from.Width, to.Width), to.Width, offset), NudgeDistance),
            Direction.Top => (ApplyOffset(MapFraction(x, from.Width, to.Width), to.Width, offset), to.Height - 1 - NudgeDistance),
            _ => (x, y)
        };
    }

    // applies an offset (percentage of destination size) to a position, clamped to safe bounds
    private static int ApplyOffset(int pos, int size, int offset)
    {
        if (offset == 0) return pos;
        return Math.Clamp(pos + (int)(offset / 100.0 * size), NudgeDistance, size - 1 - NudgeDistance);
    }

    // maps a position proportionally from one size range to another (both 0-based)
    private static int MapFraction(int pos, int fromSize, int toSize)
    {
        var fraction = (pos + 0.5) / fromSize;
        return (int)(fraction * toSize);
    }
}
