namespace Tests.Setup;

// A TimeProvider whose timers only fire when the test says so. The mouse-batch flush is armed for a
// few milliseconds of wall clock, and a test that raced it would be a test that passes on a fast
// machine — so the tests that pin the batching policy drive this instead.
public sealed class ManualTimerProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(callback, state);
        timer.Change(dueTime, period);
        lock (_timers) _timers.Add(timer);
        return timer;
    }

    // fires every armed timer once, as the runtime would at its due time
    public void FireAll()
    {
        ManualTimer[] armed;
        lock (_timers) armed = [.. _timers];
        foreach (var timer in armed) timer.FireIfArmed();
    }

    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        private readonly Lock _lock = new();
        private bool _armed;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_lock) _armed = dueTime != Timeout.InfiniteTimeSpan;
            return true;
        }

        public void FireIfArmed()
        {
            lock (_lock)
            {
                if (!_armed) return;
                _armed = false;
            }
            callback(state);
        }

        public void Dispose()
        {
            lock (_lock) _armed = false;
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
