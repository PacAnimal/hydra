using Hydra.Mouse;

namespace Tests.Mouse;

[TestFixture]
public class MouseScrollEventTests
{
    [TestCase(65536L, (short)120)]
    [TestCase(-65536L, (short)-120)]
    [TestCase(16384L, (short)30)]
    [TestCase(-16384L, (short)-30)]
    [TestCase(-16000L, (short)-29)]
    [TestCase(16000L, (short)29)]
    [TestCase(-16300L, (short)-30)]
    [TestCase(16300L, (short)30)]
    [TestCase(4096L, (short)8)]
    [TestCase(-4096L, (short)-8)]
    [TestCase(-100L, (short)0)]
    [TestCase(65536L * 1000, short.MaxValue)]
    [TestCase(-65536L * 1000, short.MinValue)]
    public void FromFixedLines_RoundsToNearestBothWays(long fixedLines, short expected)
    {
        Assert.That(MouseScrollEvent.FromFixedLines(fixedLines), Is.EqualTo(expected));
    }

    [TestCase((short)120, 65536L)]
    [TestCase((short)-120, -65536L)]
    [TestCase((short)-30, -16384L)]
    [TestCase((short)60, 32768L)]
    [TestCase((short)7, 3823L)]
    [TestCase((short)-7, -3823L)]
    public void ToFixedLines_IsOneLinePerNotch(short delta, long expected)
    {
        Assert.That(MouseScrollEvent.ToFixedLines(delta), Is.EqualTo(expected));
    }

    [TestCase((short)-30)]
    [TestCase((short)7)]
    [TestCase((short)-1)]
    [TestCase((short)-120)]
    public void RoundTrip_KeepsTheDelta(short delta)
    {
        Assert.That(MouseScrollEvent.FromFixedLines(MouseScrollEvent.ToFixedLines(delta)), Is.EqualTo(delta));
    }
}
