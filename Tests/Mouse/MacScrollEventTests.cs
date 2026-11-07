using Hydra.Mouse;
using Hydra.Platform.MacOs;
using Hydra.Relay;
using System.Runtime.Versioning;

namespace Tests.Mouse;

[TestFixture]
[Platform("MacOsX")]
[SupportedOSPlatform("macos")]
public class MacScrollEventTests
{
    [TestCase(0, -120, -1, 0)]
    [TestCase(0, 120, 1, 0)]
    [TestCase(120, 0, 0, 1)]
    [TestCase(-120, 0, 0, -1)]
    [TestCase(30, -30, 0, 0)]
    public void CreatedEvent_PreservesScrollAxes(int xDelta, int yDelta, long expectedY, long expectedX)
    {
        var msg = new MouseScrollMessage((short)xDelta, (short)yDelta);
        var eventRef = MacOutputHandler.CreateMouseScrollEvent(msg);

        Assert.That(eventRef, Is.Not.EqualTo(nint.Zero));

        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(
                    NativeMethods.CGEventGetIntegerValueField(eventRef, NativeMethods.KCGScrollWheelEventDeltaAxis1),
                    Is.EqualTo(expectedY));

                Assert.That(
                    NativeMethods.CGEventGetIntegerValueField(eventRef, NativeMethods.KCGScrollWheelEventDeltaAxis2),
                    Is.EqualTo(expectedX));

                Assert.That(
                    NativeMethods.CGEventGetIntegerValueField(eventRef, NativeMethods.KCGScrollWheelEventFixedPtDeltaAxis1),
                    Is.EqualTo((long)yDelta * 65536 / MouseScrollEvent.Notch));

                Assert.That(
                    NativeMethods.CGEventGetIntegerValueField(eventRef, NativeMethods.KCGScrollWheelEventFixedPtDeltaAxis2),
                    Is.EqualTo((long)xDelta * 65536 / MouseScrollEvent.Notch));
            }
        }
        finally
        {
            NativeMethods.CFRelease(eventRef);
        }
    }

    [Test]
    public void ZeroDelta_DoesNotCreateEvent()
    {
        var eventRef = MacOutputHandler.CreateMouseScrollEvent(
            new MouseScrollMessage(0, 0));

        Assert.That(eventRef, Is.EqualTo(nint.Zero));
    }
}