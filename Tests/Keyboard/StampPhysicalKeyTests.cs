using Hydra.Keyboard;

namespace Tests.Keyboard;

// a Resolve call stamps its key onto the events that carry none; one that already carries a key keeps it, key 0 included
[TestFixture]
public class StampPhysicalKeyTests
{
    [Test]
    public void AnUnstampedEvent_TakesTheKey()
    {
        KeyEvent?[] events = [KeyEvent.Char(KeyEventType.KeyDown, 'b', KeyModifiers.None)];

        KeyResolver.StampPhysicalKey(events, 11);

        Assert.That(events[0]!.PhysicalKey, Is.EqualTo(11));
    }

    [Test]
    public void AnEventPreStampedWithKeyZero_KeepsIt()
    {
        // macOS keycode 0 is 'a'
        KeyEvent?[] events = [KeyEvent.Char(KeyEventType.KeyUp, 'a', KeyModifiers.None) with { PhysicalKey = 0 }];

        KeyResolver.StampPhysicalKey(events, 11);

        Assert.That(events[0]!.PhysicalKey, Is.Zero);
    }
}
