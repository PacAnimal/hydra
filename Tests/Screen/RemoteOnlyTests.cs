using Hydra.Keyboard;
using Hydra.Relay;
using Hydra.Screen;
using Tests.Setup;

namespace Tests.Screen;

[TestFixture]
public class RemoteOnlyTests
{
    private FakePlatform _platform = null!;
    private FakeRelay _relay = null!;
    private InputRouter _service = null!;
    private readonly SteppableTicks _ticks = new();

    [SetUp]
    public async Task SetUp()
    {
        (_platform, _relay, _service) = TransitionTestHelper.CreateRemoteOnlyService(getTickCount: _ticks.Now);
        await _service.StartAsync(CancellationToken.None);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _service.StopAsync(CancellationToken.None);
        await _platform.DisposeAsync();
    }

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
        await _service.StopAsync(CancellationToken.None);
        (_platform, _relay, _service) = TransitionTestHelper.CreateRemoteOnlyService(TransitionTestHelper.RemoteOnlyPairConfig);
        await _service.StartAsync(CancellationToken.None);

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
        await _service.StopAsync(CancellationToken.None);
        (_platform, _relay, _service) = TransitionTestHelper.CreateRemoteOnlyService(TransitionTestHelper.RemoteOnlyPairConfig);
        await _service.StartAsync(CancellationToken.None);

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
