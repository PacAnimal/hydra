namespace Hydra.Screen;

public enum Direction { Left, Right, Top, Bottom }

public record ScreenRect(string Name, int Width, int Height, bool IsVirtual = false);

public record EdgeHit(ScreenRect Destination, Direction Direction, int EntryX, int EntryY, decimal Scale = 1.0m);
