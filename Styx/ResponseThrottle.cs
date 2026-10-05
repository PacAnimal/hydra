namespace Styx;

/// <summary>
/// The least time an endpoint a stranger can call takes to answer, whatever the answer, so a guess costs the
/// same whether it was right or wrong.
/// </summary>
public sealed class ResponseThrottle(TimeProvider clock)
{
    public Task Start(int seconds, CancellationToken cancel) => Task.Delay(TimeSpan.FromSeconds(seconds), clock, cancel);
}
