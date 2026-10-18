using Microsoft.AspNetCore.SignalR;

namespace Tests.Setup;

/// <summary>
/// A hub filter on one hub method: counts how many of its invocations run at once, says when one has started, and while
/// held parks every one that reaches it until <see cref="Release"/>. A gate, never a delay, so a test waits on a fact.
/// </summary>
public sealed class InvocationProbe(string method) : IHubFilter
{
    private TaskCompletionSource? _hold;
    private Func<HubInvocationContext, ValueTask<bool>> _which = Everything;
    private readonly RunningCounter _running = new();

    // the arguments of each invocation parked by Hold, in the order they reached it
    public NotificationQueue<object?[]> Held { get; private set; } = new();

    // the arguments of each invocation once the method has run as far as it can without waiting
    public NotificationQueue<object?[]> Started { get; private set; } = new();

    // each invocation that has ended, however it ended
    public NotificationQueue<object?[]> Finished { get; private set; } = new();

    // the most invocations of the method ever running at once
    public int MostRunning => _running.MostRunning;

    // parks the invocations `which` picks, all of them when null
    public void Hold(Func<HubInvocationContext, ValueTask<bool>>? which = null)
    {
        _which = which ?? Everything;
        _hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Release() => Interlocked.Exchange(ref _hold, null)?.TrySetResult();

    // forgets everything seen, for the next test on the same relay
    public void Reset()
    {
        Release();
        Held = new NotificationQueue<object?[]>();
        Started = new NotificationQueue<object?[]>();
        Finished = new NotificationQueue<object?[]>();
        _running.Reset();
    }

    private static ValueTask<bool> Everything(HubInvocationContext _) => ValueTask.FromResult(true);

    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext context, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        if (context.HubMethodName != method) return await next(context);

        _running.Enter();
        var arguments = context.HubMethodArguments.ToArray();
        try
        {
            if (Volatile.Read(ref _hold) is { } hold && await _which(context))
            {
                Held.Push(arguments);
                await hold.Task;
            }

            var pending = next(context);
            Started.Push(arguments);
            return await pending;
        }
        finally
        {
            _running.Exit();
            Finished.Push(arguments);
        }
    }
}
