namespace Hydra.Mouse;

// a NotchAccumulator per axis, for a slave that injects whole notches (or lines)
public sealed class ScrollAccumulator
{
    private readonly NotchAccumulator _y = new();
    private readonly NotchAccumulator _x = new();

    public ScrollNotches Take(short xDelta, short yDelta) => new(_x.Take(xDelta), _y.Take(yDelta));

    public void Reset()
    {
        _x.Reset();
        _y.Reset();
    }
}

public readonly record struct ScrollNotches(int X, int Y);
