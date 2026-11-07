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
    // from the wire message through the accumulator, as InjectMouseScroll does, so neither half can swap the axes
    [TestCase(0, -120, -1, 0, -65536, 0)]
    [TestCase(0, 120, 1, 0, 65536, 0)]
    [TestCase(120, 0, 0, 1, 0, 65536)]
    [TestCase(-120, 0, 0, -1, 0, -65536)]
    [TestCase(30, -30, 0, 0, -16384, 16384)]
    [TestCase(-180, 0, 0, -1, 0, -98304)]
    [TestCase(240, -360, -3, 2, -196608, 131072)]
    public void CreatedEvent_KeepsEachAxis(int xDelta, int yDelta, int linesY, int linesX, long fixedY, long fixedX)
    {
        var msg = new MouseScrollMessage((short)xDelta, (short)yDelta);
        var eventRef = MacOutputHandler.CreateMouseScrollEvent(msg, new ScrollAccumulator().Take(msg.XDelta, msg.YDelta), 0);
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
    public void SmoothScroll_AddsUpToAWholeLine()
    {
        var lines = new ScrollAccumulator();
        var msg = new MouseScrollMessage(0, -30);
        var deltas = new List<long>();
        for (var i = 0; i < 4; i++)
        {
            var eventRef = MacOutputHandler.CreateMouseScrollEvent(msg, lines.Take(msg.XDelta, msg.YDelta), 0);
            try
            {
                deltas.Add(NativeMethods.CGEventGetIntegerValueField(eventRef, NativeMethods.KCGScrollWheelEventDeltaAxis1));
            }
            finally
            {
                NativeMethods.CFRelease(eventRef);
            }
        }

        Assert.That(deltas, Is.EqualTo([0, 0, 0, -1]));
    }

    [Test]
    public void CreatedEvent_CarriesHeldModifiers()
    {
        const ulong flags = NativeMethods.KCGEventFlagMaskShift | NativeMethods.KCGEventFlagMaskCommand;
        var eventRef = MacOutputHandler.CreateMouseScrollEvent(new MouseScrollMessage(0, -120), new ScrollNotches(0, -1), flags);
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
        Assert.That(MacOutputHandler.CreateMouseScrollEvent(new MouseScrollMessage(0, 0), default, 0), Is.EqualTo(nint.Zero));
    }
}
