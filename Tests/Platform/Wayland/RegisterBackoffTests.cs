using Hydra.Platform.Linux.Wayland;
using Tests.Setup;

namespace Tests.Platform.Wayland;

[TestFixture]
public class RegisterBackoffTests
{
    private static readonly TimeSpan Ms = TimeSpan.FromMilliseconds(1);
    // the default schedule's waits, in ms: doubling from 50, the last cut short at the 2 s total
    private static readonly int[] DefaultDelaysMs = [50, 100, 200, 400, 800, 450];
    private static readonly int[] ExactDelaysMs = [10, 20, 40];

    [Test]
    public void Delays_DoubleFromTheFirstAndAddUpToTheTotal()
    {
        var delays = RegisterBackoff.Delays(RegisterBackoff.First, RegisterBackoff.Total).ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(delays, Is.EqualTo(DefaultDelaysMs.Select(ms => ms * Ms)));
            Assert.That(delays.Aggregate(TimeSpan.Zero, (sum, d) => sum + d), Is.EqualTo(RegisterBackoff.Total));
        }
    }

    [Test]
    public void Delays_ThatFitExactly_EndWithoutARemainder()
    {
        Assert.That(RegisterBackoff.Delays(10 * Ms, 70 * Ms), Is.EqualTo(ExactDelaysMs.Select(ms => ms * Ms)));
    }

    [Test]
    public void Delays_OfNoTotal_AreNone()
    {
        Assert.That(RegisterBackoff.Delays(10 * Ms, TimeSpan.Zero), Is.Empty);
    }

    [Test]
    public async Task Retry_ASuccessAtOnce_NeverWaits()
    {
        var time = new StartedTimers();
        var attempts = 0;

        var ok = await RegisterBackoff.Retry(() => Task.FromResult(++attempts > 0), [10 * Ms], time, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ok, Is.True);
            Assert.That(attempts, Is.EqualTo(1));
            Assert.That(time.Started.Count, Is.Zero);
        }
    }

    [Test]
    public async Task Retry_NoDelays_IsOneAttempt()
    {
        var attempts = 0;

        var ok = await RegisterBackoff.Retry(() => Task.FromResult(++attempts < 0), [], new StartedTimers(), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ok, Is.False);
            Assert.That(attempts, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Retry_WaitsEachDelayBeforeTheNextAttempt()
    {
        var time = new StartedTimers();
        var attempts = 0;

        var retrying = RegisterBackoff.Retry(() => Task.FromResult(++attempts == 3), [10 * Ms, 20 * Ms, 40 * Ms], time, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await time.Started.Next("the first wait"), Is.EqualTo(10 * Ms));
            Assert.That(attempts, Is.EqualTo(1));
        }
        time.Advance(10 * Ms);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await time.Started.Next("the second wait"), Is.EqualTo(20 * Ms));
            Assert.That(attempts, Is.EqualTo(2));
        }
        time.Advance(20 * Ms);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await retrying.WaitAsync(Bound), Is.True);
            Assert.That(attempts, Is.EqualTo(3));
            Assert.That(time.Started.Count, Is.Zero);
        }
    }

    [Test]
    public async Task Retry_FailingThroughEveryDelay_GivesUp()
    {
        var time = new StartedTimers();
        var attempts = 0;

        var retrying = RegisterBackoff.Retry(() => Task.FromResult(++attempts < 0), [10 * Ms, 20 * Ms], time, CancellationToken.None);
        time.Advance(await time.Started.Next("the first wait"));
        time.Advance(await time.Started.Next("the second wait"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await retrying.WaitAsync(Bound), Is.False);
            Assert.That(attempts, Is.EqualTo(3));
        }
    }

    [Test]
    public async Task Retry_Cancelled_StopsWaiting()
    {
        var time = new StartedTimers();
        using var cancel = new CancellationTokenSource();

        var retrying = RegisterBackoff.Retry(() => Task.FromResult(false), [10 * Ms], time, cancel.Token);
        await time.Started.Next("the wait");
        await cancel.CancelAsync();

        Assert.That(async () => await retrying.WaitAsync(Bound), Throws.InstanceOf<OperationCanceledException>());
    }
}
