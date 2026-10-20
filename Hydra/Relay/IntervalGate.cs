namespace Hydra.Relay;

// lets one caller through per interval, lock-free; clockMs defaults to Environment.TickCount64
public sealed class IntervalGate(TimeSpan interval, Func<long>? clockMs = null)
{
    private readonly long _intervalMs = (long)interval.TotalMilliseconds;
    private readonly Func<long> _clockMs = clockMs ?? (() => Environment.TickCount64);
    private long _lastMs;

    public bool TryEnter()
    {
        var now = _clockMs();
        var last = Interlocked.Read(ref _lastMs);
        return now - last >= _intervalMs && Interlocked.CompareExchange(ref _lastMs, now, last) == last;
    }
}
