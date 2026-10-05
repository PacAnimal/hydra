using Hydra.Keyboard;
using Hydra.Relay;
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

    // -- remote → remote --

    [Test]
    public async Task Locked_PreventsCrossingBetweenRemotes_AbsolutePath()
    {
        var (platform, relay, service) = await StartChain();
        platform.FireMouseMove(2559, 720);
        PressLockHotkey(platform);
        relay.ClearSent();

        for (var i = 0; i < 30; i++)
            platform.FireMouseMove(platform.WarpX + 100, platform.WarpY);

        AssertStayedOnRemote(relay);
        await StopService(platform, service);
    }

    [Test]
    public async Task Locked_PreventsCrossingBetweenRemotes_DeltaPath()
    {
        var (platform, relay, service) = await StartChain();
        platform.FireMouseMove(2559, 720);
        PressLockHotkey(platform);
        relay.ClearSent();

        for (var i = 0; i < 30; i++)
            platform.FireMouseDelta(100, 0);

        AssertStayedOnRemote(relay);
        await StopService(platform, service);
    }

    // the button query is an X round-trip on Xorg, so an edge the lock refuses must not pay for one
    [Test]
    public async Task Locked_RefusesTheEdgeWithoutQueryingButtons()
    {
        var (platform, _, service) = await StartChain();
        platform.FireMouseMove(2559, 720);
        PressLockHotkey(platform);
        var queries = platform.ButtonQueries;

        for (var i = 0; i < 30; i++)
            platform.FireMouseDelta(100, 0);

        Assert.That(platform.ButtonQueries, Is.EqualTo(queries));
        await StopService(platform, service);
    }

    [Test]
    public async Task Unlocked_CrossesBetweenRemotes_DeltaPath()
    {
        var (platform, relay, service) = await StartChain();
        platform.FireMouseMove(2559, 720);
        relay.ClearSent();

        for (var i = 0; i < 30; i++)
            platform.FireMouseDelta(100, 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(relay.Snapshot().Any(s => s.Kind == MessageKind.EnterScreen && s.Targets.Contains("remote2")), Is.True, "should enter remote2");
            Assert.That(relay.Snapshot().Any(s => s.Kind == MessageKind.LeaveScreen && s.Targets.Contains("remote")), Is.True, "should leave remote");
        }

        await StopService(platform, service);
    }

    [Test]
    public async Task ButtonHeld_PreventsCrossingBetweenRemotes_DeltaPath()
    {
        var (platform, relay, service) = await StartChain();
        platform.FireMouseMove(2559, 720);
        platform.AnyMouseButtonHeld = true;
        relay.ClearSent();

        for (var i = 0; i < 30; i++)
            platform.FireMouseDelta(100, 0);

        AssertStayedOnRemote(relay);
        await StopService(platform, service);
    }

    // -- remote-only confinement --

    [TestCase(true)]
    [TestCase(false)]
    public async Task Confined_StaysOnHostButMovesBetweenItsMonitors(bool confined)
    {
        var (platform, relay, service) = TransitionTestHelper.CreateRemoteOnlyService(TransitionTestHelper.RemoteOnlyPairConfig, headless: true);
        await service.StartAsync(CancellationToken.None);
        await relay.FirePeersChanged("mac", "win");
        await TransitionTestHelper.AdvertiseScreens(relay, "mac", "screen:0", "screen:1");
        await TransitionTestHelper.AdvertiseScreens(relay, "win", "screen:0");
        Assert.That(platform.IsOnVirtualScreen, Is.True, "pre-condition: on mac");

        if (confined)
            PressLockHotkey(platform);
        relay.ClearSent();

        // mac's two monitors are 5120 wide in total, entered at the centre of the first
        for (var i = 0; i < 60; i++)
            platform.FireMouseDelta(100, 0);

        var enters = relay.Snapshot().Where(s => s.Kind == MessageKind.EnterScreen).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(enters.Any(e => e.Targets.Contains("mac") && e.Json.Contains("screen:1")), Is.True, "should move onto mac's second monitor");
            Assert.That(enters.Any(e => e.Targets.Contains("win")), Is.EqualTo(!confined), confined ? "confinement should keep the cursor on mac" : "should cross to win");
        }

        await StopService(platform, service);
    }

    // -- helpers --

    private static void PressLockHotkey(FakePlatform platform) =>
        platform.FireKeyEvent(KeyEvent.Char(KeyEventType.KeyDown, 'l', KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super));

    private static async Task<TestServiceBundle> StartChain()
    {
        var bundle = TransitionTestHelper.CreateService(profile: TransitionTestHelper.ChainConfig);
        await bundle.Service.StartAsync(CancellationToken.None);
        await TransitionTestHelper.BringHostsOnline(bundle.Relay, ["remote", "remote2"]);
        return bundle;
    }

    private static async Task StopService(FakePlatform platform, InputRouter service)
    {
        await service.StopAsync(CancellationToken.None);
        await platform.DisposeAsync();
    }

    private static void AssertStayedOnRemote(FakeRelay relay)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(relay.Snapshot().Any(s => s.Kind == MessageKind.EnterScreen && s.Targets.Contains("remote2")), Is.False, "should not enter remote2");
            Assert.That(relay.Snapshot().Any(s => s.Kind == MessageKind.LeaveScreen && s.Targets.Contains("remote")), Is.False, "should not leave remote");
        }
    }

    private static TestServiceBundle CreateService() =>
        TransitionTestHelper.CreateService();

    private static Task BringRemoteOnline(FakeRelay relay) =>
        TransitionTestHelper.BringRemoteOnline(relay);
}
