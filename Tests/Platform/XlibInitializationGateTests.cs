using Hydra.Platform.Linux;

namespace Tests.Platform;

public class XlibInitializationGateTests
{
    [Test]
    public void TryInitialize_CallsNativeInitializerOnlyOnce()
    {
        var calls = 0;
        var gate = new XlibInitializationGate(() =>
        {
            Interlocked.Increment(ref calls);
            return 1;
        });

        Parallel.For(0, 100, _ => Assert.That(gate.TryInitialize(), Is.True));

        Assert.That(calls, Is.EqualTo(1));
    }

    [Test]
    public void TryInitialize_CachesFailure()
    {
        var calls = 0;
        var gate = new XlibInitializationGate(() =>
        {
            Interlocked.Increment(ref calls);
            return 0;
        });

        Assert.Multiple(() =>
        {
            Assert.That(gate.TryInitialize(), Is.False);
            Assert.That(gate.TryInitialize(), Is.False);
            Assert.That(calls, Is.EqualTo(1));
        });
    }
}
