using Hydra.Relay;

namespace Tests.Relay;

[TestFixture]
public class ConnectionCancellationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    // a hub close cancels with CancelAsync, whose callbacks run later on the pool, and the attempt can end
    // before they do
    [Test]
    public async Task CancelledAsTheAttemptEnds_StillReleasesWorkWaitingOnIt()
    {
        var attempt = new ConnectionCancellation(CancellationToken.None);
        var released = Released(attempt.Token);

        var closing = attempt.CancelAsync();
        attempt.Dispose();
        await closing;

        await released.WaitAsync(Timeout);
    }

    // what makes the above hold whichever runs first: the ended attempt's token is still live
    [Test]
    public void AnEndedAttempt_KeepsItsTokenLive()
    {
        var attempt = new ConnectionCancellation(CancellationToken.None);

        attempt.Dispose();

        Assert.That(attempt.Token.WaitHandle.WaitOne(0), Is.True);
    }

    [Test]
    public async Task Ending_ReleasesWorkWaitingOnIt()
    {
        var attempt = new ConnectionCancellation(CancellationToken.None);
        var released = Released(attempt.Token);

        attempt.Dispose();

        await released.WaitAsync(Timeout);
    }

    [Test]
    public async Task StoppingTheApp_CancelsTheAttempt()
    {
        using var app = new CancellationTokenSource();
        using var attempt = new ConnectionCancellation(app.Token);
        var released = Released(attempt.Token);

        await app.CancelAsync();

        await released.WaitAsync(Timeout);
    }

    private static Task Released(CancellationToken token)
    {
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        token.Register(() => released.TrySetResult());
        return released.Task;
    }
}
