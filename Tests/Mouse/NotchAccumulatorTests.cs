using Hydra.Mouse;

namespace Tests.Mouse;

[TestFixture]
public class NotchAccumulatorTests
{
    [TestCase(120, 1)]
    [TestCase(-120, -1)]
    [TestCase(360, 3)]
    [TestCase(-240, -2)]
    [TestCase(0, 0)]
    public void WholeNotches_PassStraightThrough(int delta, int expected)
    {
        var notches = new NotchAccumulator();

        Assert.That(notches.Take((short)delta), Is.EqualTo(expected));
    }

    [Test]
    public void SmoothScroll_AddsUpToWholeNotches()
    {
        var notches = new NotchAccumulator();

        Assert.That(new[] { notches.Take(-30), notches.Take(-30), notches.Take(-30), notches.Take(-30) }, Is.EqualTo([0, 0, 0, -1]));
    }

    [Test]
    public void Remainder_CarriesPastAWholeNotch()
    {
        var notches = new NotchAccumulator();

        Assert.That(new[] { notches.Take(100), notches.Take(100), notches.Take(100) }, Is.EqualTo([0, 1, 1]));
    }

    [Test]
    public void Reversal_DropsTheOtherWaysRemainder()
    {
        var notches = new NotchAccumulator();
        notches.Take(100);

        Assert.That(new[] { notches.Take(-60), notches.Take(-60) }, Is.EqualTo([0, -1]));
    }

    [Test]
    public void ZeroDelta_KeepsTheRemainder()
    {
        var notches = new NotchAccumulator();
        notches.Take(60);
        notches.Take(0);

        Assert.That(notches.Take(60), Is.EqualTo(1));
    }
}
