namespace Hydra.Mouse;

// scroll deltas in multiples of 120 (matching Windows WHEEL_DELTA convention).
// positive YDelta = scroll up (away from user), positive XDelta = scroll right.
public readonly record struct MouseScrollEvent(short XDelta, short YDelta);
