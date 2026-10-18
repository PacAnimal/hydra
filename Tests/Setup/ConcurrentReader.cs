namespace Tests.Setup;

/// <summary>Reads in a loop on the pool until disposed, so a test can write while another instance never stops reading.</summary>
internal sealed class ConcurrentReader : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _reads;

    private ConcurrentReader(Func<Task> read)
    {
        // the token, not the source, so the loop never touches a disposed source however the awaits unwind
        var token = _stop.Token;
        _reads = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested) await read();
        }, CancellationToken.None);
    }

    public static ConcurrentReader Start(Func<Task> read) => new(read);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _reads;
        _stop.Dispose();
    }
}
