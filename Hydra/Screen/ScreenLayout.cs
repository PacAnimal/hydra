namespace Hydra.Screen;

public enum Direction { Left, Right, Top, Bottom }

public record ScreenRect(string Name, int X, int Y, int Width, int Height, bool IsVirtual);

public record EdgeHit(ScreenRect Destination, Direction Direction, int EntryX, int EntryY);

public class ScreenLayout(List<ScreenRect> screens)
{
    private const int JumpZone = 1;
    private const int NudgeDistance = 2;

    // returns the screen that contains point (x, y), or null
    public ScreenRect? GetScreenAt(int x, int y) =>
        screens.FirstOrDefault(s => x >= s.X && x < s.X + s.Width && y >= s.Y && y < s.Y + s.Height);

    // checks if the cursor is in the jump zone of any edge of 'current' that has a neighbor.
    // returns the neighbor screen and mapped entry coordinates, or null.
    public EdgeHit? DetectEdgeExit(ScreenRect current, int x, int y)
    {
        Direction? dir = null;

        if (x <= current.X + JumpZone - 1)
            dir = Direction.Left;
        else if (x >= current.X + current.Width - JumpZone)
            dir = Direction.Right;
        else if (y <= current.Y + JumpZone - 1)
            dir = Direction.Top;
        else if (y >= current.Y + current.Height - JumpZone)
            dir = Direction.Bottom;

        if (dir is null)
            return null;

        var neighbor = FindNeighbor(current, dir.Value);
        if (neighbor is null)
            return null;

        var (entryX, entryY) = MapEntry(current, neighbor, dir.Value, x, y);
        return new EdgeHit(neighbor, dir.Value, entryX, entryY);
    }

    // finds the neighbor in the given direction, or null
    private ScreenRect? FindNeighbor(ScreenRect current, Direction dir)
    {
        return dir switch
        {
            // look for a screen whose left edge aligns with our right edge
            Direction.Right => screens.FirstOrDefault(s => s != current && s.X == current.X + current.Width),
            // look for a screen whose right edge aligns with our left edge
            Direction.Left => screens.FirstOrDefault(s => s != current && s.X + s.Width == current.X),
            // look for a screen whose bottom edge aligns with our top edge
            Direction.Top => screens.FirstOrDefault(s => s != current && s.Y + s.Height == current.Y),
            // look for a screen whose top edge aligns with our bottom edge
            Direction.Bottom => screens.FirstOrDefault(s => s != current && s.Y == current.Y + current.Height),
            _ => null
        };
    }

    // maps cursor position fractionally along the source edge to entry coords on the destination,
    // then nudges inward to prevent immediately re-triggering the jump zone
    private static (int entryX, int entryY) MapEntry(ScreenRect from, ScreenRect to, Direction dir, int x, int y)
    {
        return dir switch
        {
            Direction.Right => (to.X + NudgeDistance, MapFraction(y, from.Y, from.Height, to.Y, to.Height)),
            Direction.Left => (to.X + to.Width - 1 - NudgeDistance, MapFraction(y, from.Y, from.Height, to.Y, to.Height)),
            Direction.Bottom => (MapFraction(x, from.X, from.Width, to.X, to.Width), to.Y + NudgeDistance),
            Direction.Top => (MapFraction(x, from.X, from.Width, to.X, to.Width), to.Y + to.Height - 1 - NudgeDistance),
            _ => (x, y)
        };
    }

    // maps a position proportionally from one axis range to another
    private static int MapFraction(int pos, int fromOrigin, int fromSize, int toOrigin, int toSize)
    {
        var fraction = (pos - fromOrigin + 0.5) / fromSize;
        return (int)(fraction * toSize + toOrigin);
    }
}
