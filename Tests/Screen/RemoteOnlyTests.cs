using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Relay;
using Hydra.Screen;
using Tests.Setup;

namespace Tests.Screen;

[TestFixture]
public class RemoteOnlyTests
{
    private FakePlatform _platform = null!;
    private FakeRelay _relay = null!;
    private readonly SteppableTicks _ticks = new();
    private readonly StartedServices _services = new();
    // a lone screen is named after its host; a second one renames it to host:0
    private static readonly ScreenRect Home = new("pi", "pi", 0, 0, 2560, 1440, IsLocal: true);
    // small enough that the default park point (1280, 720) is off it
    private static readonly ScreenRect Small = new("pi", "pi", 0, 0, 1024, 768, IsLocal: true);

    private static LocalScreenSnapshot Screens(params ScreenRect[] screens) =>
        new([.. screens], [.. screens.Select(s => new ScreenInfoEntry(s.Name, s.X, s.Y, s.Width, s.Height, 1.0m))]);

    [SetUp]
    public async Task SetUp() => (_platform, _relay, _) = await _services.Start(TransitionTestHelper.CreateRemoteOnlyService(getTickCount: _ticks.Now));

    // replaces the router SetUp started
    private async Task Restart(TestServiceBundle bundle)
    {
        await _services.StopAll();
        (_platform, _relay, _) = await _services.Start(bundle);
    }

    private Task RestartWith(FakeScreenDetector screens) => Restart(TransitionTestHelper.CreateRemoteOnlyService(getTickCount: _ticks.Now, screens: screens));

    [TearDown]
    public Task TearDown() => _services.StopAll();

    // -- auto-entry --

    [Test]
    public async Task AutoEnters_WhenScreenInfoReceived()
    {
        await TransitionTestHelper.BringHostOnline(_relay, "mac");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_platform.IsOnVirtualScreen, Is.True, "should be on virtual screen after ScreenInfo");
            Assert.That(_platform.HideCursorCalled, Is.True, "should hide cursor on entry");
        }
    }

    [Test]
    public async Task AutoEntry_ParksTheCursor()
    {
        // a platform that recentres itself (Windows) recentres on the warp target, which nothing sets until the first warp
        _platform.Calls.Clear();

        await TransitionTestHelper.BringHostOnline(_relay, "mac");

        Assert.That(_platform.Calls.Where(c => c is "flag:True" or "warp"), Is.EqualTo(["flag:True", "warp"]));
    }

    [Test]
    public async Task AutoEntry_Headless_HasNothingToPark()
    {
        await Restart(TransitionTestHelper.CreateRemoteOnlyService(headless: true, getTickCount: _ticks.Now));

        await TransitionTestHelper.BringHostOnline(_relay, "mac");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_platform.IsOnVirtualScreen, Is.True, "pre-condition: entered");
            Assert.That(_platform.WarpCount, Is.Zero);
        }
    }

    [Test]
    public async Task ScreensAppearingDuringAHeadlessVisit_AreParkedOn()
    {
        // started with no screen (display off at boot), entered the remote, then a monitor appears
        var screens = new FakeScreenDetector { Snapshot = Screens() };
        await RestartWith(screens);
        await TransitionTestHelper.BringHostOnline(_relay, "mac");
        _platform.Calls.Clear();

        screens.Snapshot = Screens(Small);
        await screens.FireChange();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_platform.Calls.Where(c => c == "warp"), Is.EqualTo(["warp"]));
            Assert.That((_platform.WarpX, _platform.WarpY), Is.EqualTo((512, 384)));
        }
    }

    [Test]
    public async Task ParkPointLeftOffEveryScreenDuringAVisit_IsMoved()
    {
        var screens = new FakeScreenDetector { Snapshot = Screens(Home) };
        await RestartWith(screens);
        await TransitionTestHelper.BringHostOnline(_relay, "mac");

        screens.Snapshot = Screens(Small);
        await screens.FireChange();

        Assert.That((_platform.WarpX, _platform.WarpY), Is.EqualTo((512, 384)));
    }

    [Test]
    public async Task ScreenAddedDuringAVisit_KeepsTheParkPoint()
    {
        // the new monitor renames the parked one, but the park point is still on it
        var screens = new FakeScreenDetector { Snapshot = Screens(Home) };
        await RestartWith(screens);
        await TransitionTestHelper.BringHostOnline(_relay, "mac");
        _platform.Calls.Clear();

        screens.Snapshot = Screens(Home with { Name = "pi:0" }, Home with { Name = "pi:1", X = 2560 });
        await screens.FireChange();

        Assert.That(_platform.Calls, Has.No.Member("warp"));
    }

    [Test]
    public async Task AutoEntry_WhileAButtonIsHeld_WaitsForItsRelease()
    {
        // the button went down here; entering now would swallow its up and leave it stuck down
        _platform.AnyMouseButtonHeld = true;
        await TransitionTestHelper.BringHostOnline(_relay, "mac");
        Assert.That(_platform.IsOnVirtualScreen, Is.False, "not while the button is held");

        _platform.AnyMouseButtonHeld = false;
        _platform.FireMouseButton(new MouseButtonEvent(MouseButton.Left, false));

        Assert.That(_platform.IsOnVirtualScreen, Is.True);
    }

    [Test]
    public async Task AutoEntryRefusedForAHeldButton_IsRetriedOnTheNextLocalMove()
    {
        // the release itself can be handled before the OS has the button up, so a move retries too
        _platform.AnyMouseButtonHeld = true;
        await TransitionTestHelper.BringHostOnline(_relay, "mac");
        _platform.AnyMouseButtonHeld = false;

        _platform.FireMouseMove(100, 100);

        Assert.That(_platform.IsOnVirtualScreen, Is.True);
    }

    [Test]
    public async Task LockHotkey_RelockWhileAButtonIsHeld_StaysLocalUntilItsRelease()
    {
        await TransitionTestHelper.BringHostOnline(_relay, "mac");
        _platform.FireKeyEvent(KeyEvent.Char(KeyEventType.KeyDown, 'l', KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super));
        Assert.That(_platform.IsOnVirtualScreen, Is.False, "pre-condition: unlocked");
        _platform.AnyMouseButtonHeld = true;

        _platform.FireKeyEvent(KeyEvent.Char(KeyEventType.KeyDown, 'l', KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super));
        var whileHeld = _platform.IsOnVirtualScreen;
        _platform.AnyMouseButtonHeld = false;
        _platform.FireMouseButton(new MouseButtonEvent(MouseButton.Left, false));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(whileHeld, Is.False, "refused while the button is held");
            Assert.That(_platform.IsOnVirtualScreen, Is.True, "entered on its release");
        }
    }

    [Test]
    public async Task DoesNotAutoEnter_WhenRelayDisconnected()
    {
        _relay.Connected = false;
        await TransitionTestHelper.BringHostOnline(_relay, "mac");

        Assert.That(_platform.IsOnVirtualScreen, Is.False, "should not enter when relay disconnected");
    }

    [Test]
    public async Task SendsEnterScreen_OnAutoEntry()
    {
        await TransitionTestHelper.BringHostOnline(_relay, "mac");

        var enters = _relay.Snapshot().Where(s => s.Kind == MessageKind.EnterScreen).ToList();
        Assert.That(enters, Is.Not.Empty, "should send EnterScreen on auto-entry");
        Assert.That(enters[0].Targets, Contains.Item("mac"));
    }

    // -- mouse delta routing --

    [Test]
    public async Task MouseDelta_SendsMousePosition_WhenOnVirtualScreen()
    {
        await TransitionTestHelper.BringHostOnline(_relay, "mac");
        Assert.That(_platform.IsOnVirtualScreen, Is.True);

        _relay.ClearSent();
        _ticks.Advance(20); // exceed throttle interval
        _platform.FireMouseDelta(10, 5);

        var moves = _relay.Snapshot().Where(s => s.Kind is MessageKind.MouseMove or MessageKind.MouseMoveDelta).ToList();
        Assert.That(moves, Is.Not.Empty, "delta should produce a mouse send");
    }

    [Test]
    public async Task MouseDelta_Ignored_WhenNotOnVirtualScreen()
    {
        await TransitionTestHelper.BringHostOnline(_relay, "mac");

        // unlock — cursor leaves virtual screen
        _platform.FireKeyEvent(KeyEvent.Char(KeyEventType.KeyDown, 'l',
            KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super));
        Assert.That(_platform.IsOnVirtualScreen, Is.False, "pre-condition: not on virtual screen");

        _relay.ClearSent();
        _platform.FireMouseDelta(100, 0);

        var moves = _relay.Snapshot().Where(s => s.Kind is MessageKind.MouseMove or MessageKind.MouseMoveDelta).ToList();
        Assert.That(moves, Is.Empty, "delta should be ignored when not on virtual screen");
    }

    // -- lock hotkey --

    [Test]
    public async Task LockHotkey_Unlock_LeavesVirtualScreen()
    {
        await TransitionTestHelper.BringHostOnline(_relay, "mac");
        Assert.That(_platform.IsOnVirtualScreen, Is.True, "pre-condition: on virtual screen");

        _platform.ShowCursorCalled = false;
        _platform.FireKeyEvent(KeyEvent.Char(KeyEventType.KeyDown, 'l',
            KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_platform.IsOnVirtualScreen, Is.False, "should leave virtual screen after unlock");
            Assert.That(_platform.ShowCursorCalled, Is.True, "should show cursor after unlock");
        }
    }

    [Test]
    public async Task LockHotkey_Unlock_ShowsBeforeLeavingAndKeepsTheCursorWhereItIs()
    {
        await TransitionTestHelper.BringHostOnline(_relay, "mac");
        _platform.Calls.Clear();

        _platform.FireKeyEvent(KeyEvent.Char(KeyEventType.KeyDown, 'l',
            KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super));

        Assert.That(_platform.Calls.Where(c => c is "show" or "flag:False" or "warp"), Is.EqualTo(["show", "flag:False"]));
    }

    [Test]
    public async Task LockHotkey_Relock_ReEntersVirtualScreen()
    {
        await TransitionTestHelper.BringHostOnline(_relay, "mac");

        // unlock
        _platform.FireKeyEvent(KeyEvent.Char(KeyEventType.KeyDown, 'l',
            KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super));
        Assert.That(_platform.IsOnVirtualScreen, Is.False, "pre-condition: unlocked");

        // relock
        _platform.HideCursorCalled = false;
        _platform.FireKeyEvent(KeyEvent.Char(KeyEventType.KeyDown, 'l',
            KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_platform.IsOnVirtualScreen, Is.True, "should re-enter virtual screen after relock");
            Assert.That(_platform.HideCursorCalled, Is.True, "should hide cursor on re-entry");
        }
    }

    [Test]
    public async Task LockHotkey_SendsLeaveScreen_OnUnlock()
    {
        await TransitionTestHelper.BringHostOnline(_relay, "mac");
        _relay.ClearSent();

        _platform.FireKeyEvent(KeyEvent.Char(KeyEventType.KeyDown, 'l',
            KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super));

        var leaves = _relay.Snapshot().Where(s => s.Kind == MessageKind.LeaveScreen).ToList();
        Assert.That(leaves, Is.Not.Empty, "should send LeaveScreen on unlock");
    }

    // -- disconnect + reconnect --

    [Test]
    public async Task Disconnect_LeavesVirtualScreen()
    {
        await TransitionTestHelper.BringHostOnline(_relay, "mac");
        Assert.That(_platform.IsOnVirtualScreen, Is.True, "pre-condition");

        _platform.ShowCursorCalled = false;
        await _relay.FireDisconnected();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_platform.IsOnVirtualScreen, Is.False, "should leave virtual screen on disconnect");
            Assert.That(_platform.ShowCursorCalled, Is.True, "should show cursor on disconnect");
        }
    }

    [Test]
    public async Task Reconnect_AutoReEnters_IfLocked()
    {
        await TransitionTestHelper.BringHostOnline(_relay, "mac");
        await _relay.FireDisconnected();
        Assert.That(_platform.IsOnVirtualScreen, Is.False, "pre-condition: disconnected");

        _platform.HideCursorCalled = false;
        await TransitionTestHelper.BringHostOnline(_relay, "mac");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_platform.IsOnVirtualScreen, Is.True, "should re-enter on reconnect");
            Assert.That(_platform.HideCursorCalled, Is.True, "should hide cursor on re-entry");
        }
    }

    [Test]
    public async Task Reconnect_DoesNotReEnter_IfUnlocked()
    {
        await TransitionTestHelper.BringHostOnline(_relay, "mac");

        // unlock before disconnect
        _platform.FireKeyEvent(KeyEvent.Char(KeyEventType.KeyDown, 'l',
            KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super));
        await _relay.FireDisconnected();

        _platform.HideCursorCalled = false;
        await TransitionTestHelper.BringHostOnline(_relay, "mac");

        Assert.That(_platform.HideCursorCalled, Is.False, "should not re-enter when user explicitly unlocked");
    }

    // -- multi-remote host transitions --

    [Test]
    public async Task MouseDelta_TransitionsToNeighborHost()
    {
        await Restart(TransitionTestHelper.CreateRemoteOnlyService(TransitionTestHelper.RemoteOnlyPairConfig));

        await TransitionTestHelper.BringHostsOnline(_relay, ["mac", "win"]);
        Assert.That(_platform.IsOnVirtualScreen, Is.True, "pre-condition: on mac");

        _relay.ClearSent();

        // push delta far enough right to hit the edge of mac (2560 wide) and cross to win
        for (var i = 0; i < 30; i++)
            _platform.FireMouseDelta(100, 0);

        var enters = _relay.Snapshot().Where(s => s.Kind == MessageKind.EnterScreen).ToList();
        Assert.That(enters.Any(e => e.Targets.Contains("win")), Is.True, "should send EnterScreen to 'win' after crossing edge");
    }

    [Test]
    public async Task MouseDelta_SendsLeaveScreen_WhenCrossingToNewHost()
    {
        await Restart(TransitionTestHelper.CreateRemoteOnlyService(TransitionTestHelper.RemoteOnlyPairConfig));

        await TransitionTestHelper.BringHostsOnline(_relay, ["mac", "win"]);
        _relay.ClearSent();

        for (var i = 0; i < 30; i++)
            _platform.FireMouseDelta(100, 0);

        var leaves = _relay.Snapshot().Where(s => s.Kind == MessageKind.LeaveScreen && s.Targets.Contains("mac")).ToList();
        Assert.That(leaves, Is.Not.Empty, "should send LeaveScreen to 'mac' when crossing to 'win'");
    }

    // -- startup --

    [Test]
    public void StartAsync_RemoteOnly_NoLocalHostEntry_StartsWithoutError()
    {
        // config has no entry for "pi" in hosts — this is valid for remote-only
        Assert.That(_platform.IsAccessibilityTrusted(), Is.True, "sanity: fake platform is trusted");
        // the fact that SetUp completed without error is the main assertion
    }
}
