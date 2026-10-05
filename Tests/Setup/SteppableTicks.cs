namespace Tests.Setup;

/// <summary>
/// The router's tick clock, running at wall-clock speed but steppable, so a test that needs the mouse throttle
/// interval to have passed says so instead of sleeping through it. Real time underneath keeps every other test
/// in the fixture on the clock it was written against.
/// </summary>
internal sealed class SteppableTicks
{
    private long _skew;

    internal long Now() => Environment.TickCount64 + Interlocked.Read(ref _skew);

    internal void Advance(int milliseconds) => Interlocked.Add(ref _skew, milliseconds);
}
