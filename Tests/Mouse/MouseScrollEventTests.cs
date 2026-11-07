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

    [TestCase((short)120, (short)-120)]
    [TestCase((short)-30, (short)30)]
    [TestCase((short)0, (short)0)]
    [TestCase(short.MaxValue, (short)-short.MaxValue)]
    [TestCase(short.MinValue, short.MaxValue)]
    public void Invert_FlipsTheSign_ClampingTheOneWithoutAMirror(short delta, short expected)
    {
        Assert.That(MouseScrollEvent.Invert(delta), Is.EqualTo(expected));
    }

    [Test]
    public void MacHorizontal_RunsOppositeTheWire()
    {
        // the wire's right (Windows' WM_MOUSEHWHEEL, X11's button 7) is macOS's left
        Assert.That(MouseScrollEvent.MacHorizontal(120), Is.EqualTo(-120));
    }

    [Test]
    public void MacHorizontal_BothWays_LeavesMacToMacUnchanged()
    {
        Assert.That(MouseScrollEvent.MacHorizontal(MouseScrollEvent.MacHorizontal(-90)), Is.EqualTo(-90));
    }
}
