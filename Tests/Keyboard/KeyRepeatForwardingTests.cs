using Hydra.Keyboard;
using Hydra.Relay;
using Tests.Setup;

namespace Tests.Keyboard;

// a key forwarded to the screen being visited keeps whether it was a repeat
[TestFixture]
public class KeyRepeatForwardingTests
{
    private readonly StartedServices _services = new();
    private TestServiceBundle _bundle = null!;

    [SetUp]
    public async Task SetUp()
    {
        _bundle = await _services.Visiting(TransitionTestHelper.CreateService());
        _bundle.Relay.ClearSent();
    }

    [TearDown]
    public Task TearDown() => _services.StopAll();

    [Test]
    public void KeyDown_ForwardedNotMarkedRepeat()
    {
        _bundle.Platform.FireKeyEvent(KeyEvent.Char(KeyEventType.KeyDown, 'w', KeyModifiers.None));

        var forwarded = _bundle.Relay.Sent<KeyEventMessage>(MessageKind.KeyEvent);
        Assert.That(forwarded, Has.Count.GreaterThanOrEqualTo(1));
        Assert.That(forwarded[0].IsRepeat, Is.False, "an initial press is not a repeat");
    }

    [Test]
    public void RepeatKeyEvent_ForwardedMarkedRepeat()
    {
        // an OS auto-repeat is re-resolved on the master and surfaces as a KeyEvent flagged IsRepeat
        _bundle.Platform.FireKeyEvent(KeyEvent.Char(KeyEventType.KeyDown, 'w', KeyModifiers.None));
        _bundle.Platform.FireKeyEvent(KeyEvent.Char(KeyEventType.KeyDown, 'w', KeyModifiers.None) with { IsRepeat = true });

        var forwarded = _bundle.Relay.Sent<KeyEventMessage>(MessageKind.KeyEvent);
        Assert.That(forwarded, Has.Count.EqualTo(2));
        Assert.That(forwarded[1].IsRepeat, Is.True, "a repeat key event is forwarded marked as a repeat");
    }
}
