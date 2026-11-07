using System.Runtime.Versioning;
using Hydra.Mouse;
using Hydra.Platform.MacOs;
using Hydra.Relay;

namespace Tests.Platform;

[TestFixture]
[Platform("MacOsX")]
[SupportedOSPlatform("macos")]
public class MacScrollEventTests
{
    // from the wire message, as InjectMouseScroll does, so nothing between can swap the axes. the wire's right is
    // macOS's left, so the horizontal fields carry the opposite sign
    [TestCase(0, -120, -1, 0, -65536, 0)]
    [TestCase(0, 120, 1, 0, 65536, 0)]
    [TestCase(120, 0, 0, -1, 0, -65536)]
    [TestCase(-120, 0, 0, 1, 0, 65536)]
    [TestCase(30, -30, 0, 0, -16384, -16384)]
    [TestCase(-180, 0, 0, 1, 0, 98304)]
    [TestCase(240, -360, -3, -2, -196608, -131072)]
    public void CreatedEvent_KeepsEachAxis(int xDelta, int yDelta, int linesY, int linesX, long fixedY, long fixedX)
    {
        var eventRef = MacOutputHandler.CreateMouseScrollEvent(new MouseScrollMessage((short)xDelta, (short)yDelta), new ScrollAccumulator(), 0);
        Assert.That(eventRef, Is.Not.EqualTo(nint.Zero));

        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(NativeMethods.CGEventGetIntegerValueField(eventRef, NativeMethods.KCGScrollWheelEventDeltaAxis1), Is.EqualTo(linesY));
                Assert.That(NativeMethods.CGEventGetIntegerValueField(eventRef, NativeMethods.KCGScrollWheelEventDeltaAxis2), Is.EqualTo(linesX));
                Assert.That(NativeMethods.CGEventGetIntegerValueField(eventRef, NativeMethods.KCGScrollWheelEventFixedPtDeltaAxis1), Is.EqualTo(fixedY));
                Assert.That(NativeMethods.CGEventGetIntegerValueField(eventRef, NativeMethods.KCGScrollWheelEventFixedPtDeltaAxis2), Is.EqualTo(fixedX));
            }
        }
        finally
        {
            NativeMethods.CFRelease(eventRef);
        }
    }

    [Test]
    public void SmoothScroll_AddsUpToAWholeLine() =>
        Assert.That(LineDeltas(new MouseScrollMessage(0, -30), 4, NativeMethods.KCGScrollWheelEventDeltaAxis1), Is.EqualTo([0, 0, 0, -1]));

    [Test]
    public void SmoothHorizontalScroll_AddsUpToAWholeLine_TheMacWayRound() =>
        Assert.That(LineDeltas(new MouseScrollMessage(40, 0), 3, NativeMethods.KCGScrollWheelEventDeltaAxis2), Is.EqualTo([0, 0, -1]));

    [Test]
    public void CreatedEvent_CarriesHeldModifiers()
    {
        const ulong flags = NativeMethods.KCGEventFlagMaskShift | NativeMethods.KCGEventFlagMaskCommand;
        var eventRef = MacOutputHandler.CreateMouseScrollEvent(new MouseScrollMessage(0, -120), new ScrollAccumulator(), flags);
        Assert.That(eventRef, Is.Not.EqualTo(nint.Zero));

        try
        {
            Assert.That(NativeMethods.CGEventGetFlags(eventRef) & flags, Is.EqualTo(flags));
        }
        finally
        {
            NativeMethods.CFRelease(eventRef);
        }
    }

    [Test]
    public void ZeroDelta_DoesNotCreateEvent()
    {
        Assert.That(MacOutputHandler.CreateMouseScrollEvent(new MouseScrollMessage(0, 0), new ScrollAccumulator(), 0), Is.EqualTo(nint.Zero));
    }

    // a master reads macOS's horizontal the other way round onto the wire, from a wheel or a trackpad
    [TestCase(false, 0, -1, 0, -120)]
    [TestCase(false, 1, 0, -120, 0)]
    [TestCase(false, -2, 3, 240, 360)]
    [TestCase(true, 1, 0, -120, 0)]
    [TestCase(true, 0, -1, 0, -120)]
    public void Master_ReadsEachAxisOntoTheWire(bool continuous, int linesX, int linesY, int expectedX, int expectedY)
    {
        var eventRef = MacScroll(linesX, linesY, continuous);
        try
        {
            Assert.That(MacInputHandler.WireScroll(eventRef, accelerate: true), Is.EqualTo(new MouseScrollEvent((short)expectedX, (short)expectedY)));
        }
        finally
        {
            NativeMethods.CFRelease(eventRef);
        }
    }

    [Test]
    public void Master_TrackpadFraction_CrossesAsAFractionOfANotch()
    {
        var eventRef = MacScroll(0, 0, continuous: true, fixedX: 16384, fixedY: -32768);
        try
        {
            Assert.That(MacInputHandler.WireScroll(eventRef, accelerate: true), Is.EqualTo(new MouseScrollEvent(-30, -60)));
        }
        finally
        {
            NativeMethods.CFRelease(eventRef);
        }
    }

    // a Mac master to a Mac slave flips twice, so the slave scrolls the way the master did
    [TestCase(2, -3)]
    [TestCase(-1, 0)]
    [TestCase(0, 1)]
    public void MacToMac_LandsAsItLeft(int linesX, int linesY)
    {
        var sent = MacScroll(linesX, linesY, continuous: false);
        nint landed = 0;
        try
        {
            var wire = MacInputHandler.WireScroll(sent, accelerate: true);
            landed = MacOutputHandler.CreateMouseScrollEvent(new MouseScrollMessage(wire.XDelta, wire.YDelta), new ScrollAccumulator(), 0);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(NativeMethods.CGEventGetIntegerValueField(landed, NativeMethods.KCGScrollWheelEventDeltaAxis2), Is.EqualTo(linesX));
                Assert.That(NativeMethods.CGEventGetIntegerValueField(landed, NativeMethods.KCGScrollWheelEventDeltaAxis1), Is.EqualTo(linesY));
            }
        }
        finally
        {
            NativeMethods.CFRelease(sent);
            if (landed != 0) NativeMethods.CFRelease(landed);
        }
    }

    // a scroll as macOS reports one: whole lines, and for a trackpad the fixed-point fields it reads instead
    private static nint MacScroll(int linesX, int linesY, bool continuous, long? fixedX = null, long? fixedY = null)
    {
        var eventRef = NativeMethods.CGEventCreateScrollWheelEvent2(nint.Zero, 1, 2, linesY, linesX, 0);
        Assert.That(eventRef, Is.Not.EqualTo(nint.Zero));
        NativeMethods.CGEventSetIntegerValueField(eventRef, NativeMethods.KCGScrollWheelEventIsContinuous, continuous ? 1 : 0);
        NativeMethods.CGEventSetIntegerValueField(eventRef, NativeMethods.KCGScrollWheelEventFixedPtDeltaAxis1, fixedY ?? linesY * 65536L);
        NativeMethods.CGEventSetIntegerValueField(eventRef, NativeMethods.KCGScrollWheelEventFixedPtDeltaAxis2, fixedX ?? linesX * 65536L);
        return eventRef;
    }

    // the whole-line field of each of count events made from the same message, sharing one accumulator
    private static List<long> LineDeltas(MouseScrollMessage msg, int count, int field)
    {
        var lines = new ScrollAccumulator();
        var deltas = new List<long>();
        for (var i = 0; i < count; i++)
        {
            var eventRef = MacOutputHandler.CreateMouseScrollEvent(msg, lines, 0);
            try
            {
                deltas.Add(NativeMethods.CGEventGetIntegerValueField(eventRef, field));
            }
            finally
            {
                NativeMethods.CFRelease(eventRef);
            }
        }
        return deltas;
    }
}
