namespace Tests.Setup;

/// <summary>
/// Parks whatever reaches it while held, until the test releases it. A gate, never a delay, so a test waits on a fact.
/// </summary>
public sealed class Gate
{
    private TaskCompletionSource? _hold;

    public Gate(bool held = false)
    {
        if (held) Hold();
    }

    public bool IsHeld => Volatile.Read(ref _hold) != null;

    public void Hold() => Interlocked.CompareExchange(ref _hold, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), null);

    public void Release() => Interlocked.Exchange(ref _hold, null)?.TrySetResult();

    // at once while open, else once released
    public Task Passed(CancellationToken cancel = default) => Volatile.Read(ref _hold)?.Task.WaitAsync(cancel) ?? Task.CompletedTask;
}
