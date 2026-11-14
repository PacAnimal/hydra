using System.Text.Json;
using Cathedral.Config;
using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Relay;
using Microsoft.Extensions.Time.Testing;
using Tests.Setup;

namespace Tests.Screen;

// a button, scroll or key never overtakes motion the send throttle still holds back: the slave gets the final aim first
[TestFixture]
public class MotionOrderTests : ServicesFixture
{
    private FakePlatform _platform = null!;
    private FakeRelay _relay = null!;
    private readonly FakeTimeProvider _time = new();

    [SetUp]
    public async Task SetUp()
    {
        // a fake clock: the throttle's trailing send never fires on its own, so what these tests hold back stays held
        (_platform, _relay, _) = await Services.Visiting(TransitionTestHelper.CreateService(timeProvider: _time));
    }

    // past the throttle once, so the next move sends; the clock then stands still, so every later move is held back
    private void PastTheThrottle() => _time.Advance(TimeSpan.FromSeconds(1));

    [Test]
    public void RelativeMode_TheHeldBackDeltaGoesBeforeTheClick()
    {
        _platform.PressHotkey('m');
        _relay.Sent.Clear();
        PastTheThrottle();

        for (var i = 0; i < 4; i++)
            _platform.FireMouseDelta(-7.5, 0);
        Click();

        var sent = _relay.Sent.Snapshot();
        var button = sent.FindIndex(s => s.Kind == MessageKind.MouseButton);
        var deltasBefore = sent.Take(button).Where(s => s.Kind == MessageKind.MouseMoveDelta)
            .Select(s => JsonSerializer.Deserialize<MouseMoveDeltaMessage>(s.Json, SaneJson.Options)!).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(button, Is.GreaterThan(0), "a button is sent, after the motion");
            Assert.That(deltasBefore, Has.Count.GreaterThan(1), "the throttle held some motion back");
            Assert.That(deltasBefore.Sum(d => d.Dx), Is.EqualTo(-30), "all of the aim arrives before the shot");
            Assert.That(sent.Skip(button + 1).Count(s => s.Kind == MessageKind.MouseMoveDelta), Is.Zero);
        }
    }

    [Test]
    public void AbsoluteMode_TheLatestPositionGoesBeforeTheClick()
    {
        _relay.Sent.Clear();
        PastTheThrottle();

        for (var i = 0; i < 4; i++)
            _platform.FireMouseMove(_platform.WarpX + 5, _platform.WarpY);
        Click();

        var sent = _relay.Sent.Snapshot();
        var button = sent.FindIndex(s => s.Kind == MessageKind.MouseButton);
        var moves = sent.Take(button).Where(s => s.Kind == MessageKind.MouseMove)
            .Select(s => JsonSerializer.Deserialize<MouseMoveMessage>(s.Json, SaneJson.Options)!).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(button, Is.GreaterThan(0));
            Assert.That(moves, Has.Count.EqualTo(2), "the first move, then the one the throttle held back");
            Assert.That(moves[^1].X, Is.EqualTo(moves[0].X + 15), "the click lands where the cursor ended up");
        }
    }

    [Test]
    public void AStillMouse_SendsNoExtraMoveWithTheClick()
    {
        PastTheThrottle();
        _platform.FireMouseMove(_platform.WarpX + 5, _platform.WarpY);
        _relay.Sent.Clear();

        Click();
        _platform.FireMouseScroll(new MouseScrollEvent(0, 1));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_relay.Sent.Of(MessageKind.MouseMove), Is.Empty, "the position already went out");
            Assert.That(_relay.Sent.Of(MessageKind.MouseButton), Has.Count.EqualTo(2));
        }
    }

    [Test]
    public void TogglingWithNothingHeldBack_SendsNoMove()
    {
        PastTheThrottle();
        _platform.FireMouseMove(_platform.WarpX + 5, _platform.WarpY);
        _relay.Sent.Clear();

        // an absolute move here is an XTest jump an X11 game reads as raw motion
        _platform.PressHotkey('m');
        _platform.PressHotkey('m');

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_relay.Sent.Of(MessageKind.MouseMove), Is.Empty);
            Assert.That(_relay.Sent.Of(MessageKind.MouseMoveDelta), Is.Empty);
        }
    }

    [Test]
    public void TogglingWithAHeldBackPosition_SendsItOnce()
    {
        PastTheThrottle();
        for (var i = 0; i < 4; i++)
            _platform.FireMouseMove(_platform.WarpX + 5, _platform.WarpY);
        var sentFirst = _relay.Sent.Of<MouseMoveMessage>(MessageKind.MouseMove).Last();
        _relay.Sent.Clear();

        _platform.PressHotkey('m');

        var moves = _relay.Sent.Of<MouseMoveMessage>(MessageKind.MouseMove);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(moves, Has.Count.EqualTo(1));
            Assert.That(moves[0].X, Is.EqualTo(sentFirst.X + 15), "where the cursor ended up, not where it was last sent");
            Assert.That(_relay.Sent.Of(MessageKind.MouseMoveDelta), Is.Empty, "absolute motion goes out as a position, not into the new mode");
        }
    }

    [Test]
    public void AKey_GoesAfterTheHeldBackDelta()
    {
        _platform.PressHotkey('m');
        _relay.Sent.Clear();
        PastTheThrottle();

        for (var i = 0; i < 4; i++)
            _platform.FireMouseDelta(-10, 0);
        _platform.FireKeyEvent(KeyEvent.Char(KeyEventType.KeyDown, 'w', KeyModifiers.None));

        var sent = _relay.Sent.Snapshot();
        var key = sent.FindIndex(s => s.Kind == MessageKind.KeyEvent);
        var dx = sent.Take(key).Where(s => s.Kind == MessageKind.MouseMoveDelta)
            .Sum(s => JsonSerializer.Deserialize<MouseMoveDeltaMessage>(s.Json, SaneJson.Options)!.Dx);
        Assert.That(dx, Is.EqualTo(-40));
    }

    private void Click()
    {
        _platform.FireMouseButton(new MouseButtonEvent(MouseButton.Left, true));
        _platform.FireMouseButton(new MouseButtonEvent(MouseButton.Left, false));
    }
}
