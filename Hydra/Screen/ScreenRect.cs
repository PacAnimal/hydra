namespace Hydra.Screen;

public enum Direction { Left, Right, Top, Bottom }

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
    bool IsLocal)     // true = local screen on this machine
    : IBounded
{
    public ScreenBounds Bounds => new(X, Y, Width, Height);

    public bool Contains(double x, double y) =>
        x >= X && x < X + Width && y >= Y && y < Y + Height;

    public static bool ScreenListChanged(IReadOnlyList<IBounded> a, IReadOnlyList<IBounded> b)
    {
        if (a.Count != b.Count) return true;
        for (var i = 0; i < a.Count; i++)
            if (a[i].Bounds != b[i].Bounds) return true;
        return false;
    }
}

public record EdgeHit(ScreenRect Destination, Direction Direction, int EntryX, int EntryY);
