namespace Hydra.Platform.Linux.Wayland;

// A start every caller shares (WaylandSession's portal session): callers get the one under way or done, and after one
// fails, its failure until SenderAcquisition's backoff runs out, so a portal dialog the user cancelled is not shown again
// on every call. Thread-safe.
internal sealed class SharedStart<T>(Func<Task<T>> start, TimeProvider time) where T : class
{
    private readonly Lock _lock = new();
    private readonly SenderAcquisition _attempts = new(time);
    private Task<T>? _current;

    internal Task<T> Get()
    {
        lock (_lock)
        {
            if (_current is { IsFaulted: false, IsCanceled: false } current) return current;
            if (_attempts.Begin() is not { } epoch) return _current ?? Task.FromException<T>(new InvalidOperationException("No start to share"));
            var started = Settled(epoch);
            _current = started;
            // a start every caller stopped waiting for still fails somewhere: observed here, logged by the starter
            _ = started.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            return started;
        }
    }

    // the started thing is gone (a lost session): the next Get starts afresh at once
    internal void Forget()
    {
        lock (_lock)
        {
            _current = null;
            _attempts.Lost();
        }
    }

    // for disposal: the start there was, if any, none after it
    internal Task<T>? Take()
    {
        lock (_lock)
        {
            var current = _current;
            _current = null;
            return current;
        }
    }

    // the start, its outcome recorded before anyone awaiting it sees it
    private async Task<T> Settled(int epoch)
    {
        await Task.Yield();
        try
        {
            var result = await start();
            lock (_lock) _attempts.Succeeded(epoch);
            return result;
        }
        catch (OperationCanceledException)
        {
            // shutdown, or a start given up on: no backoff
            lock (_lock) _attempts.Lost();
            throw;
        }
        catch
        {
            lock (_lock) _attempts.Failed(epoch);
            throw;
        }
    }
}
