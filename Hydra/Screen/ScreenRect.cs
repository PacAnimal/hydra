using Cathedral.Extensions;

namespace Hydra.Screen;

public enum Direction { Left, Right, Up, Down }

public static class DirectionExtensions
{
    public static Direction Opposite(this Direction dir) => dir switch
    {
        Direction.Left => Direction.Right,
        Direction.Right => Direction.Left,
        Direction.Up => Direction.Down,
        Direction.Down => Direction.Up,
        _ => throw new ArgumentOutOfRangeException(nameof(dir), dir, null),
    };

    public static bool IsVertical(this Direction dir) => dir is Direction.Up or Direction.Down;

    public static bool IsPerpendicularTo(this Direction dir, Direction other) => dir.IsVertical() != other.IsVertical();
}

public class ScreenIdentity
{
    public required string ScreenName { get; init; }
    public string? Output { get; init; }
    public string? DisplayName { get; init; }
    public string? PlatformId { get; init; }

    public bool Matches(string id) =>
        ScreenName.EqualsIgnoreCase(id)
        || Output?.EqualsIgnoreCase(id) is true
        || DisplayName?.EqualsIgnoreCase(id) is true
        || PlatformId?.EqualsIgnoreCase(id) is true;
}

public record ScreenBounds(int X, int Y, int Width, int Height);

public interface IBounded
{
    ScreenBounds Bounds { get; }
}

public record ScreenRect(
    string Name,      // unique id, e.g. "host:0", "host:1"
    string Host,      // hostname for relay routing
    int X, int Y,     // top-left in host coordinate space
    int Width, int Height,
    bool IsLocal,     // true = local screen on this machine
    ScreenIdentity? Identity = null)
    : IBounded
{
    public ScreenBounds Bounds => new(X, Y, Width, Height);

    public bool Contains(double x, double y) =>
        x >= X && x < X + Width && y >= Y && y < Y + Height;

    public ScreenPoint ToGlobal(ScreenPoint local) => new(X + local.X, Y + local.Y);

    public static bool ScreenListChanged(IReadOnlyList<IBounded> a, IReadOnlyList<IBounded> b)
    {
        if (a.Count != b.Count) return true;
        for (var i = 0; i < a.Count; i++)
            if (a[i].Bounds != b[i].Bounds) return true;
        return false;
    }
}

// Entry: where the cursor lands on the destination (screen-local). Exit: the edge it left by, for a return that has no
// edge of its own (a disconnect, the screensaver) to come back through, see ScreenLayout.LandingBack.
public record EdgeHit(ScreenRect Destination, Direction Direction, ScreenPoint Entry, ScreenEdge Exit);

// a position along one edge of a named screen
public readonly record struct ScreenEdge(string Screen, Direction Edge, int Position);

// a pixel position, global or screen-local as its owner says
public readonly record struct ScreenPoint(int X, int Y);
