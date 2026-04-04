namespace Hydra.Mouse;

// scroll deltas in 120-unit convention (one standard notch = 120; sub-120 values for smooth scroll).
// positive YDelta = scroll up (away from user), positive XDelta = scroll right.
public readonly record struct MouseScrollEvent(short XDelta, short YDelta);
