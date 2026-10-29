using Hydra.Config;
using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Relay;
using Tests.Setup;

namespace Tests.Screen;

[TestFixture]
public class ScreenLockTests : ServicesFixture
{
    private FakePlatform _platform = null!;

    [SetUp]
    public async Task SetUp() => (_platform, _, _) = await Services.StartOnline(TransitionTestHelper.CreateService());

    // -- lock toggle --

    [Test]
    public async Task LockHotkey_TogglesLock()
    {
        Assert.That(_platform.IsOnVirtualScreen, Is.False);

        // push cursor to right edge — should transition
        TransitionTestHelper.EnterRemote(_platform);

        // return to real screen
        _platform.FireMouseMove(_platform.WarpX, _platform.WarpY);
        _platform.IsOnVirtualScreen = false;

        // restart fresh
        await Services.StopAll();
        await SetUp();

        // lock
        _platform.PressHotkey('l');

        // attempt transition — should be blocked
        _platform.HideCursorCalled = false;
        _platform.FireMouseMove(2559, 720);
        Assert.That(_platform.HideCursorCalled, Is.False, "transition should be blocked when locked");

        // unlock
        _platform.PressHotkey('l');

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
        _platform.PressHotkey('l', up: true);

        _platform.HideCursorCalled = false;
        _platform.FireMouseMove(2559, 720);
        Assert.That(_platform.HideCursorCalled, Is.True, "key-up should not toggle lock");
    }

    [Test]
    public async Task LockHotkeyOnAMediaKey_ItsRepeatDoesNotToggleAgain()
    {
        // volume and brightness auto-repeat; the macOS tap marks those downs IsRepeat
        var bundle = await Services.StartOnline(TransitionTestHelper.CreateService(profile: TransitionTestHelper.ProfileWith(
            hotkeys: new() { [nameof(HotkeyAction.ToggleCursorLock)] = [nameof(SpecialKey.AudioVolumeUp)] })));

        var down = KeyEvent.Special(KeyEventType.KeyDown, SpecialKey.AudioVolumeUp, KeyModifiers.None);
        bundle.Platform.FireKeyEvent(down);
        bundle.Platform.FireKeyEvent(down with { IsRepeat = true });

        bundle.Platform.FireMouseMove(2559, 720);
        Assert.That(bundle.Platform.HideCursorCalled, Is.False, "a repeat must not unlock");
    }

    [Test]
    public void Locked_PreventsReturnToRealScreen()
    {
        TransitionTestHelper.EnterRemote(_platform);

        // lock while on virtual screen
        _platform.PressHotkey('l');

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
        var (platform, relay, _) = await VisitingChain();
        platform.PressHotkey('l');
        relay.Sent.Clear();

        for (var i = 0; i < 30; i++)
            platform.FireMouseMove(platform.WarpX + 100, platform.WarpY);

        AssertStayedOnRemote(relay);
    }

    [Test]
    public async Task Locked_PreventsCrossingBetweenRemotes_DeltaPath()
    {
        var (platform, relay, _) = await VisitingChain();
        platform.PressHotkey('l');
        relay.Sent.Clear();

        for (var i = 0; i < 30; i++)
            platform.FireMouseDelta(100, 0);

        AssertStayedOnRemote(relay);
    }

    // the button query is an X round-trip on Xorg, so an edge the lock refuses must not pay for one
    [Test]
    public async Task Locked_RefusesTheEdgeWithoutQueryingButtons()
    {
        var (platform, _, _) = await VisitingChain();
        platform.PressHotkey('l');
        // a held button on a platform that sees it would be asked about, unless the lock refuses first
        platform.SeesSwallowedButtons = true;
        platform.FireMouseButton(new MouseButtonEvent(MouseButton.Left, true));
        var queries = platform.ButtonQueries;

        for (var i = 0; i < 30; i++)
            platform.FireMouseDelta(100, 0);

        Assert.That(platform.ButtonQueries, Is.EqualTo(queries));
    }

    [Test]
    public async Task Unlocked_CrossesBetweenRemotes_DeltaPath()
    {
        var (platform, relay, _) = await VisitingChain();
        relay.Sent.Clear();

        for (var i = 0; i < 30; i++)
            platform.FireMouseDelta(100, 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(relay.Sent.Of<EnterScreenMessage>(MessageKind.EnterScreen, "remote2"), Is.Not.Empty, "should enter remote2");
            Assert.That(relay.Sent.Of(MessageKind.LeaveScreen, "remote"), Is.Not.Empty, "should leave remote");
        }
    }

    [Test]
    public async Task ButtonHeld_PreventsCrossingBetweenRemotes_DeltaPath()
    {
        var (platform, relay, _) = await VisitingChain();
        platform.FireMouseButton(new MouseButtonEvent(MouseButton.Left, true));
        relay.Sent.Clear();

        for (var i = 0; i < 30; i++)
            platform.FireMouseDelta(100, 0);

        AssertStayedOnRemote(relay);
    }

    // -- remote-only confinement --

    [TestCase(true)]
    [TestCase(false)]
    public async Task Confined_StaysOnHostButMovesBetweenItsMonitors(bool confined)
    {
        var (platform, relay, _) = await Services.Start(
            TransitionTestHelper.CreateRemoteOnlyService(TransitionTestHelper.RemoteOnlyPairConfig, headless: true));
        await relay.FirePeersChanged("mac", "win");
        await TransitionTestHelper.AdvertiseScreens(relay, "mac", 2);
        await TransitionTestHelper.AdvertiseScreens(relay, "win", 1);
        Assert.That(platform.IsOnVirtualScreen, Is.True, "pre-condition: on mac");

        if (confined)
            platform.PressHotkey('l');
        relay.Sent.Clear();

        // mac's two monitors are 5120 wide in total, entered at the centre of the first
        for (var i = 0; i < 60; i++)
            platform.FireMouseDelta(100, 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(relay.Sent.Of<EnterScreenMessage>(MessageKind.EnterScreen, "mac").Select(e => e.Screen), Does.Contain("mac:1"),
                "should move onto mac's second monitor");
            Assert.That(relay.Sent.Of<EnterScreenMessage>(MessageKind.EnterScreen, "win"), confined ? Is.Empty : Is.Not.Empty,
                confined ? "confinement should keep the cursor on mac" : "should cross to win");
        }
    }

    // -- helpers --

    // the chain master, with the cursor on remote
    private Task<TestServiceBundle> VisitingChain() => Services.Visiting(
        TransitionTestHelper.CreateService(profile: TransitionTestHelper.ChainConfig), "remote", "remote2");

    private static void AssertStayedOnRemote(FakeRelay relay)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(relay.Sent.Of<EnterScreenMessage>(MessageKind.EnterScreen, "remote2"), Is.Empty, "should not enter remote2");
            Assert.That(relay.Sent.Of(MessageKind.LeaveScreen, "remote"), Is.Empty, "should not leave remote");
        }
    }
}
