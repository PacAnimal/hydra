using Hydra.Config;
using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Platform;
using Hydra.Relay;
using Hydra.Screen;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Screen;

[TestFixture]
public class ScreenLockTests
{
    private FakePlatform _platform = null!;
    private ScreenTransitionService _service = null!;

    private static readonly HydraConfig TestConfig = new()
    {
        Mode = Mode.Master,
        Screens = [new ScreenRect("main", 0, 0, 0, 0, false), new ScreenRect("right", 0, 0, 0, 0, true)]
    };

    [SetUp]
    public async Task SetUp()
    {
        _platform = new FakePlatform();
        _service = new ScreenTransitionService(_platform, TestConfig, new NullRelaySender(), NullLoggerFactory.Instance, NullLogger<ScreenTransitionService>.Instance);
        await _service.StartAsync(CancellationToken.None);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _service.StopAsync(CancellationToken.None);
        _platform.Dispose();
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
        _platform.FireMouseMove(_platform.WarpX, _platform.WarpY); // trigger warp-center path
        _platform.IsOnVirtualScreen = false; // simulate return
        _service.StopAsync(CancellationToken.None).Wait();

        // restart fresh
        _platform.Reset();
        _service = new ScreenTransitionService(_platform, TestConfig, new NullRelaySender(), NullLoggerFactory.Instance, NullLogger<ScreenTransitionService>.Instance);
        _service.StartAsync(CancellationToken.None).Wait();

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

        // simulate movement past virtual screen left edge — return should be blocked
        // The virtual screen is at x=2560..5119; left edge is x=2560
        // Apply enough movement to cross back: start from entry, move far left
        // We need to drive the virtual mouse past the left edge via delta accumulation.
        // Simulate multiple warp-cycle moves leftward by warping cursor slightly left of center each time.
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

    // -- stub --

    private sealed class FakePlatform : IPlatformInput
    {
        private Action<double, double>? _onMouseMove;
        private Action<KeyEvent>? _onKeyEvent;

        public bool IsOnVirtualScreen { get; set; }
        public bool HideCursorCalled { get; set; }
        public bool ShowCursorCalled { get; set; }
        public int WarpX { get; private set; }
        public int WarpY { get; private set; }

        public void FireMouseMove(double x, double y) => _onMouseMove?.Invoke(x, y);
        public void FireKeyEvent(KeyEvent e) => _onKeyEvent?.Invoke(e);

        public void Reset()
        {
            IsOnVirtualScreen = false;
            HideCursorCalled = false;
            ShowCursorCalled = false;
        }

        public ScreenRect GetPrimaryScreenBounds() => new("main", 0, 0, 2560, 1440, false);
        public bool IsAccessibilityTrusted() => true;

        public void StartEventTap(
            Action<double, double> onMouseMove,
            Action<KeyEvent> onKeyEvent,
            Action<MouseButtonEvent> onMouseButton,
            Action<MouseScrollEvent> onMouseScroll)
        {
            _onMouseMove = onMouseMove;
            _onKeyEvent = onKeyEvent;
            WarpX = 2560 / 2;
            WarpY = 1440 / 2;
        }

        public void StopEventTap() { }
        public void WarpCursor(int x, int y) { WarpX = x; WarpY = y; }
        public void HideCursor() { HideCursorCalled = true; }
        public void ShowCursor() { ShowCursorCalled = true; }
        public void Dispose() { }
    }
}
