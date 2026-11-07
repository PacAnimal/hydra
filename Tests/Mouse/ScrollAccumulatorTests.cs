using Hydra.Mouse;

namespace Tests.Mouse;

[TestFixture]
public class ScrollAccumulatorTests
{
    [TestCase(120, 0, 1, 0)]
    [TestCase(0, 120, 0, 1)]
    [TestCase(-240, 360, -2, 3)]
    public void EachAxis_KeepsItsOwnNotches(int xDelta, int yDelta, int expectedX, int expectedY)
    {
        Assert.That(new ScrollAccumulator().Take((short)xDelta, (short)yDelta), Is.EqualTo(new ScrollNotches(expectedX, expectedY)));
    }

    [Test]
    public void EachAxis_CarriesItsOwnRemainder()
    {
        var scroll = new ScrollAccumulator();
        scroll.Take(60, -90);

        Assert.That(scroll.Take(60, -30), Is.EqualTo(new ScrollNotches(1, -1)));
    }
}
