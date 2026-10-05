using System.Threading.Tasks.Sources;

namespace Tests.Setup;

/// <summary>
/// Hands out ValueTasks that complete only once their caller is parked awaiting them, so a caller that moves
/// on without awaiting has two calls outstanding when the next arrives, however long producing it takes.
/// </summary>
internal sealed class AwaitedCalls
{
    private int _count;
    private int _outstanding;
    private int _mostOutstanding;

    public int Count => Volatile.Read(ref _count);
    public int MostOutstanding => Volatile.Read(ref _mostOutstanding);

    public ValueTask Next()
    {
        Interlocked.Increment(ref _count);
        var outstanding = Interlocked.Increment(ref _outstanding);
        int most;
        while (outstanding > (most = Volatile.Read(ref _mostOutstanding)) && Interlocked.CompareExchange(ref _mostOutstanding, outstanding, most) != most) { }
        var call = new CompletesWhenAwaited(() => Interlocked.Decrement(ref _outstanding));
        return new ValueTask(call, call.Version);
    }

    private sealed class CompletesWhenAwaited(Action onAwaited) : IValueTaskSource
    {
        private ManualResetValueTaskSourceCore<bool> _core = new() { RunContinuationsAsynchronously = true };

        public short Version => _core.Version;
        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);
        public void GetResult(short token) => _core.GetResult(token);

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            _core.OnCompleted(continuation, state, token, flags);
            onAwaited();
            _core.SetResult(true);
        }
    }
}
