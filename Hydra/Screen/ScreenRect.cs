namespace Hydra.Screen;

public enum Direction { Left, Right, Top, Bottom }

public record ScreenRect(string Name, int X, int Y, int Width, int Height, bool IsVirtual);

public record EdgeHit(ScreenRect Destination, Direction Direction, int EntryX, int EntryY);
