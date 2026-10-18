using Hydra.Relay;
using Microsoft.Extensions.Time.Testing;

namespace Tests.Relay;

[TestFixture]
public class IntervalGateTests
{
    [Test]
    public void LetsOneCallerThroughPerInterval()
    {
        var time = new FakeTimeProvider();
        var gate = new IntervalGate(TimeSpan.FromSeconds(1), () => time.GetUtcNow().ToUnixTimeMilliseconds());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(gate.TryEnter(), Is.True);
            Assert.That(gate.TryEnter(), Is.False);
            time.Advance(TimeSpan.FromMilliseconds(999));
            Assert.That(gate.TryEnter(), Is.False);
            time.Advance(TimeSpan.FromMilliseconds(1));
            Assert.That(gate.TryEnter(), Is.True);
        }
    }

    [Test]
    public void ConcurrentCallersShareOneEntry()
    {
        var gate = new IntervalGate(TimeSpan.FromSeconds(1), () => 10_000L);
        var entered = 0;

        Parallel.For(0, 64, _ =>
        {
            if (gate.TryEnter())
                Interlocked.Increment(ref entered);
        });

        Assert.That(entered, Is.EqualTo(1));
    }
}
