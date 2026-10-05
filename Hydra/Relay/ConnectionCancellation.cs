namespace Hydra.Relay;

/// <summary>
/// One connection attempt's cancellation, followed by the app's own. The source is never disposed: a hub
/// close cancels it with <c>CancelAsync</c>, which runs the callbacks later on the pool, and the attempt
/// can unwind before they do. Disposing it then drops them, and work started on the connection waits on a
/// token that has been cancelled but never fires.
/// </summary>
internal sealed class ConnectionCancellation : IDisposable
{
    private readonly CancellationTokenSource _source = new();
    private readonly CancellationTokenRegistration _app;

    public ConnectionCancellation(CancellationToken app) =>
        _app = app.Register(static source => ((CancellationTokenSource)source!).Cancel(), _source);

    public CancellationToken Token => _source.Token;
    public bool IsCancellationRequested => _source.IsCancellationRequested;

    public void Cancel() => _source.Cancel();
    public Task CancelAsync() => _source.CancelAsync();

    // the attempt is over: anything still waiting on it is released
    public void Dispose()
    {
        _app.Dispose();
        _source.Cancel();
    }
}
