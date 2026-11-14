using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Linux.Wayland;

// One lane of portal calls, run one at a time in order off the caller's thread. Each item is bounded by its timeout, so
// a portal that never answers holds the lane that long at most; a failure or timeout is logged and the lane goes on.
internal sealed class SerialWork(ILogger log, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private Task _tail = Task.CompletedTask;

    // completes once everything queued so far has run
    internal Task Idle
    {
        get
        {
            lock (_lock) return _tail;
        }
    }

    // true when work completed; false when it failed, timed out or was cancelled
    internal Task<bool> Enqueue(string what, Func<CancellationToken, Task> work, TimeSpan timeout, CancellationToken cancel)
    {
        lock (_lock)
        {
            var run = _tail.ContinueWith(_ => Run(what, work, timeout, cancel), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            _tail = run;
            return run;
        }
    }

    private async Task<bool> Run(string what, Func<CancellationToken, Task> work, TimeSpan timeout, CancellationToken cancel)
    {
        if (cancel.IsCancellationRequested) return false;
        using var deadline = new CancellationTokenSource(timeout, _time);
        using var either = CancellationTokenSource.CreateLinkedTokenSource(cancel, deadline.Token);
        try
        {
            // a call that takes no token (Enable, Release) is abandoned at the deadline rather than waited for
            await work(either.Token).WaitAsync(either.Token);
            return true;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception e) when (e is TimeoutException || (e is OperationCanceledException && deadline.IsCancellationRequested))
        {
            if (log.IsEnabled(LogLevel.Warning)) log.LogWarning("{What}: the desktop portal did not answer within {Timeout}", what, timeout);
            return false;
        }
        catch (Exception e)
        {
            if (log.IsEnabled(LogLevel.Warning)) log.LogWarning(e, "{What} failed", what);
            return false;
        }
    }
}
