namespace Hydra.Mouse;

// whole notches out of a stream of scroll deltas, carrying the rest to the next, so a smooth scroll's
// fractions of a notch add up rather than each rounding to nothing
public sealed class NotchAccumulator
{
    private int _remainder;

    public int Take(short delta)
    {
        // a reversal starts afresh
        if (delta != 0 && Math.Sign(delta) != Math.Sign(_remainder)) _remainder = 0;
        _remainder += delta;
        var notches = _remainder / MouseScrollEvent.Notch;
        _remainder -= notches * MouseScrollEvent.Notch;
        return notches;
    }
}
