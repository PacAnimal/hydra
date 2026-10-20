namespace Hydra.Mouse;

// scroll deltas in Notch units (smaller values for smooth scroll).
// positive YDelta = scroll up (away from user), positive XDelta = scroll right.
public readonly record struct MouseScrollEvent(short XDelta, short YDelta)
{
    // one standard wheel notch, as on Windows (WHEEL_DELTA)
    public const short Notch = 120;
}
