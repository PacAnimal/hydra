namespace Hydra.Platform;

// accumulates sub-120 scroll deltas and returns whole clicks (1 click = 120 units)
internal struct ScrollAccumulator
{
    private int _acc;

    // add delta and return the number of whole clicks consumed (positive = forward/right, negative = back/left)
    public int Add(int delta)
    {
        _acc += delta;
        var clicks = _acc / 120;
        _acc -= clicks * 120;
        return clicks;
    }
}
