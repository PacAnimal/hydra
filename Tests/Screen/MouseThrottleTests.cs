using System.Text.Json;
using Hydra.Keyboard;
using Hydra.Relay;
using Hydra.Screen;
using Tests.Setup;

namespace Tests.Screen;

[TestFixture]
public class MouseThrottleTests
{
    private FakePlatform _platform = null!;
    private FakeRelay _relay = null!;
    private InputRouter _service = null!;

    [SetUp]
    public async Task SetUp()
    {
        (_platform, _relay, _service) = CreateService();
        await _service.StartAsync(CancellationToken.None);
        await BringRemoteOnline(_relay);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _service.StopAsync(CancellationToken.None);
        _platform.Dispose();
    }

    [Test]
    public void MouseMoves_AreThrottled_ToMaxHz()
    {
        // enter virtual screen
        _platform.FireMouseMove(2559, 720);
        Assert.That(_platform.IsOnVirtualScreen, Is.True);
        _relay.Sent.Clear();

        var warpX = _platform.WarpX;
        var warpY = _platform.WarpY;

        // fire 50 rapid mouse moves (well under 1000/120 ≈ 8.33ms apart in real time,
        // but TickCount64 ticks fast enough that we just verify at most one send fires)
        for (var i = 0; i < 50; i++)
            _platform.FireMouseMove(warpX + 5, warpY);

        var mouseMoves = _relay.Sent.Count(s => s.Kind == MessageKind.MouseMove);
        // should be significantly fewer than 50 — throttle kicks in
        Assert.That(mouseMoves, Is.LessThanOrEqualTo(5),
            $"Expected throttling but got {mouseMoves} MouseMove sends for 50 events");
    }

    [Test]
    public void RelativeMouseToggle_Hotkey_SendsDeltaMessages()
    {
        // enter virtual screen
        _platform.FireMouseMove(2559, 720);
        Assert.That(_platform.IsOnVirtualScreen, Is.True);

        // toggle to relative mode with Ctrl+Alt+Super+M
        _platform.FireKeyEvent(KeyEvent.Char(KeyEventType.KeyDown, 'm',
            KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super));

        _relay.Sent.Clear();
        var warpX = _platform.WarpX;
        var warpY = _platform.WarpY;

        // wait past the throttle interval to ensure a send happens
        Thread.Sleep(20);
        _platform.FireMouseMove(warpX + 10, warpY + 5);
        Thread.Sleep(20);
        _platform.FireMouseMove(warpX + 5, warpY);

        var deltaMessages = _relay.Sent.Where(s => s.Kind == MessageKind.MouseMoveDelta).ToList();
        var absoluteMessages = _relay.Sent.Where(s => s.Kind == MessageKind.MouseMove).ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deltaMessages, Is.Not.Empty, "expected MouseMoveDelta messages in relative mode");
            Assert.That(absoluteMessages, Is.Empty, "expected no MouseMove messages in relative mode");
        }
    }

    [Test]
    public void RelativeMouseToggle_TogglesBackToAbsolute()
    {
        // enter virtual screen
        _platform.FireMouseMove(2559, 720);
        Assert.That(_platform.IsOnVirtualScreen, Is.True);

        // toggle to relative
        _platform.FireKeyEvent(KeyEvent.Char(KeyEventType.KeyDown, 'm',
            KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super));

        // toggle back to absolute
        _platform.FireKeyEvent(KeyEvent.Char(KeyEventType.KeyDown, 'm',
            KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super));

        _relay.Sent.Clear();
        var warpX = _platform.WarpX;
        var warpY = _platform.WarpY;

        Thread.Sleep(20);
        _platform.FireMouseMove(warpX + 10, warpY);

        var absoluteMessages = _relay.Sent.Where(s => s.Kind == MessageKind.MouseMove).ToList();
        Assert.That(absoluteMessages, Is.Not.Empty, "expected MouseMove (absolute) after toggling back");
    }

    [Test]
    public void RelativeMouseToggle_WhenNotOnVirtualScreen_DoesNothing()
    {
        Assert.That(_platform.IsOnVirtualScreen, Is.False);

        // toggle while not on virtual screen — should silently do nothing
        Assert.DoesNotThrow(() =>
            _platform.FireKeyEvent(KeyEvent.Char(KeyEventType.KeyDown, 'm',
                KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super)));
    }

    [Test]
    public void KeyDown_IncludesRepeatSettings()
    {
        // enter virtual screen
        _platform.FireMouseMove(2559, 720);
        _relay.Sent.Clear();

        _platform.FireKeyEvent(KeyEvent.Char(KeyEventType.KeyDown, 'w', KeyModifiers.None));

        var keySends = _relay.Sent.Where(s => s.Kind == MessageKind.KeyEvent).ToList();
        Assert.That(keySends, Has.Count.GreaterThanOrEqualTo(1));

        var msg = JsonSerializer.Deserialize<KeyEventMessage>(keySends[0].Json, Cathedral.Config.SaneJson.Options);
        Assert.That(msg, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(msg!.RepeatDelayMs, Is.Not.Null, "RepeatDelayMs should be set on KeyDown");
            Assert.That(msg.RepeatRateMs, Is.Not.Null, "RepeatRateMs should be set on KeyDown");
            Assert.That(msg.RepeatDelayMs, Is.EqualTo(500)); // FakePlatform returns (500, 33)
            Assert.That(msg.RepeatRateMs, Is.EqualTo(33));
        }
    }

    [Test]
    public void KeyUp_DoesNotIncludeRepeatSettings()
    {
        // enter virtual screen
        _platform.FireMouseMove(2559, 720);
        _relay.Sent.Clear();

        _platform.FireKeyEvent(KeyEvent.Char(KeyEventType.KeyUp, 'w', KeyModifiers.None));

        var keySends = _relay.Sent.Where(s => s.Kind == MessageKind.KeyEvent).ToList();
        Assert.That(keySends, Has.Count.GreaterThanOrEqualTo(1));

        var msg = JsonSerializer.Deserialize<KeyEventMessage>(keySends[0].Json, Cathedral.Config.SaneJson.Options);
        Assert.That(msg, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(msg!.RepeatDelayMs, Is.Null, "RepeatDelayMs should be null on KeyUp");
            Assert.That(msg.RepeatRateMs, Is.Null, "RepeatRateMs should be null on KeyUp");
        }
    }

    // -- helpers --

    private static TestServiceBundle CreateService() =>
        TransitionTestHelper.CreateService();

    private static Task BringRemoteOnline(FakeRelay relay) =>
        TransitionTestHelper.BringRemoteOnline(relay);
}
