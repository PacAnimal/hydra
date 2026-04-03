namespace Hydra.Screen;

public enum Direction { Left, Right, Top, Bottom }

public record ScreenRect(
    string Name,      // unique id, e.g. "host:0", "host:1"
    string Host,      // hostname for relay routing
    int X, int Y,     // top-left in host coordinate space
    int Width, int Height,
    bool IsLocal)     // true = local screen on this machine
{
    public (int X, int Y, int Width, int Height) Bounds => (X, Y, Width, Height);

    public bool Contains(double x, double y) =>
        x >= X && x < X + Width && y >= Y && y < Y + Height;
}

public record EdgeHit(ScreenRect Destination, Direction Direction, int EntryX, int EntryY);
