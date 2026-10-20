namespace Styx;

/// <summary>
/// The least time an endpoint a stranger can call takes to answer, whatever the answer, so a guess costs the
/// same whether it was right or wrong.
/// </summary>
public sealed class ResponseThrottle(TimeProvider clock)
{
    public Task Start(int seconds, CancellationToken cancel) => Task.Delay(TimeSpan.FromSeconds(seconds), clock, cancel);

    // the answer, no sooner than the throttle a caller started
    public static async Task<T> After<T>(Task throttle, T answer)
    {
        await throttle;
        return answer;
    }
}
