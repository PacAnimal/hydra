using Hydra.Keyboard;
using Hydra.Screen;
using Tests.Setup;

namespace Tests.Screen;

[TestFixture]
public class ScreenLockTests
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
        await _platform.DisposeAsync();
    }

    // -- lock toggle --

    [Test]
    public void LockHotkey_TogglesLock()
    {
        Assert.That(_platform.IsOnVirtualScreen, Is.False);

        // push cursor to right edge — should transition
        _platform.FireMouseMove(2559, 720);
        Assert.That(_platform.IsOnVirtualScreen, Is.True, "should enter virtual before lock");

        // return to real screen
        _platform.FireMouseMove(_platform.WarpX, _platform.WarpY);
        _platform.IsOnVirtualScreen = false;
        _service.StopAsync(CancellationToken.None).Wait();

        // restart fresh
        (_platform, _relay, _service) = CreateService();
        _service.StartAsync(CancellationToken.None).Wait();
        TransitionTestHelper.BringRemoteOnline(_relay).Wait();

        // lock
        _platform.FireKeyEvent(KeyEvent.Char(KeyEventType.KeyDown, 'l',
            KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super));

        // attempt transition — should be blocked
        _platform.HideCursorCalled = false;
        _platform.FireMouseMove(2559, 720);
        Assert.That(_platform.HideCursorCalled, Is.False, "transition should be blocked when locked");

        // unlock
        _platform.FireKeyEvent(KeyEvent.Char(KeyEventType.KeyDown, 'l',
            KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super));

        // transition should work again
        _platform.HideCursorCalled = false;
        _platform.FireMouseMove(2559, 720);
        Assert.That(_platform.HideCursorCalled, Is.True, "transition should work after unlock");
    }

    [Test]
    public void LockHotkey_WrongModifiers_DoesNotLock()
    {
        // ctrl+L only — missing Alt and Super
        _platform.FireKeyEvent(KeyEvent.Char(KeyEventType.KeyDown, 'l', KeyModifiers.Control));

        _platform.HideCursorCalled = false;
        _platform.FireMouseMove(2559, 720);
        Assert.That(_platform.HideCursorCalled, Is.True, "transition should still work with wrong modifiers");
    }

    [Test]
    public void LockHotkey_KeyUp_DoesNotToggle()
    {
        // key-up event should not toggle
        _platform.FireKeyEvent(KeyEvent.Char(KeyEventType.KeyUp, 'l',
            KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super));

        _platform.HideCursorCalled = false;
        _platform.FireMouseMove(2559, 720);
        Assert.That(_platform.HideCursorCalled, Is.True, "key-up should not toggle lock");
    }

    [Test]
    public void Locked_PreventsReturnToRealScreen()
    {
        // enter virtual screen
        _platform.FireMouseMove(2559, 720);
        Assert.That(_platform.IsOnVirtualScreen, Is.True);

        // lock while on virtual screen
        _platform.FireKeyEvent(KeyEvent.Char(KeyEventType.KeyDown, 'l',
            KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super));

        var warpX = _platform.WarpX;
        var warpY = _platform.WarpY;
        _platform.ShowCursorCalled = false;

        // each move: cursor is at warpX-50 (50px left of center), produces dx=-50
        for (var i = 0; i < 60; i++)
            _platform.FireMouseMove(warpX - 50, warpY);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_platform.ShowCursorCalled, Is.False, "should not return to real screen when locked");
            Assert.That(_platform.IsOnVirtualScreen, Is.True, "should remain on virtual screen when locked");
        }
    }

    // -- helpers --

    private static TestServiceBundle CreateService() =>
        TransitionTestHelper.CreateService();

    private static Task BringRemoteOnline(FakeRelay relay) =>
        TransitionTestHelper.BringRemoteOnline(relay);
}
