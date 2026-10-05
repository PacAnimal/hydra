namespace Tests.Setup;

/// <summary>
/// Every timer due at once. For the relay's response throttle, which would otherwise hold each login for a
/// second; <c>ResponseThrottleTests</c> pins the throttle itself.
/// </summary>
public sealed class InstantTimeProvider : TimeProvider
{
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        base.CreateTimer(callback, state, TimeSpan.Zero, period);
}
