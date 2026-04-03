using System.Text.Json;
using Cathedral.Config;
using Hydra.Config;
using Hydra.Keyboard;
using Hydra.Relay;
using Hydra.Screen;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.Screen;

[TestFixture]
public class ScreenLockTests
{
    private FakePlatform _platform = null!;
    private FakeRelay _relay = null!;
    private ScreenTransitionService _service = null!;

    // "home" is the local screen; "remote" is a real remote host
    private static readonly HydraConfig TestConfig = new()
    {
        Mode = Mode.Master,
        Name = "home",
        Hosts =
        [
            new HostConfig
            {
                Name = "home",
                Neighbours = [new NeighbourConfig { Direction = Direction.Right, Name = "remote" }],
            },
            new HostConfig
            {
                Name = "remote",
                Neighbours = [new NeighbourConfig { Direction = Direction.Left, Name = "home" }],
            },
        ],
    };

    [SetUp]
    public async Task SetUp()
    {
        (_platform, _relay, _service) = CreateService();
        await _service.StartAsync(CancellationToken.None);
        BringRemoteOnline(_relay);
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
        _platform.FireMouseMove(_platform.WarpX, _platform.WarpY);
        _platform.IsOnVirtualScreen = false;
        _service.StopAsync(CancellationToken.None).Wait();

        // restart fresh
        (_platform, _relay, _service) = CreateService();
        _service.StartAsync(CancellationToken.None).Wait();
        BringRemoteOnline(_relay);

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

    private static (FakePlatform, FakeRelay, ScreenTransitionService) CreateService()
    {
        var platform = new FakePlatform();
        var relay = new FakeRelay();
        var service = new ScreenTransitionService(platform, TestConfig, relay, NullLoggerFactory.Instance, NullLogger<ScreenTransitionService>.Instance);
        return (platform, relay, service);
    }

    private static void BringRemoteOnline(FakeRelay relay)
    {
        relay.FirePeersChanged("remote");
        var info = JsonSerializer.Serialize(new ScreenInfoMessage([new ScreenInfoEntry("screen:0", 0, 0, 2560, 1440, 1.0m)]), SaneJson.Options);
        relay.FireMessageReceived("remote", MessageKind.ScreenInfo, info);
    }
}
