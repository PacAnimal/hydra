namespace Hydra.Mouse;

// scroll deltas in Notch units (smaller values for smooth scroll).
// positive YDelta = scroll up (away from user), positive XDelta = scroll right.
public readonly record struct MouseScrollEvent(short XDelta, short YDelta)
{
    // one standard wheel notch, as on Windows (WHEEL_DELTA)
    public const short Notch = 120;

    // macOS's 16.16 fixed-point line delta to wire units, one line per notch. both ways round to nearest, so
    // neither direction is biased and a delta survives the round trip
    public static short FromFixedLines(long fixedLines) =>
        (short)Math.Clamp(Math.Round(fixedLines * (double)Notch / 65536, MidpointRounding.AwayFromZero), short.MinValue, short.MaxValue);

    public static long ToFixedLines(short delta) => (long)Math.Round(delta * 65536.0 / Notch, MidpointRounding.AwayFromZero);
}
