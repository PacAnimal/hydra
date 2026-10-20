using Microsoft.Extensions.Time.Testing;

namespace Tests.Setup;

// reports each timer as it starts, with its due time, so a test advances only a wait that exists
public sealed class StartedTimers : FakeTimeProvider
{
    public NotificationQueue<TimeSpan> Started { get; } = new();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        Started.Push(dueTime);
        return timer;
    }
}
