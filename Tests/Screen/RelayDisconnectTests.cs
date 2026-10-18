using Hydra.Relay;
using Tests.Setup;

namespace Tests.Screen;

[TestFixture]
public class RelayDisconnectTests : ServicesFixture
{
    private FakePlatform _platform = null!;
    private FakeRelay _relay = null!;

    [SetUp]
    public async Task SetUp() => (_platform, _relay, _) = await Services.StartOnline(TransitionTestHelper.CreateService());

    [Test]
    public async Task RelayDisconnect_WhileOnVirtualScreen_SnapsBackAndShowsCursor()
    {
        TransitionTestHelper.EnterRemote(_platform);

        _platform.ShowCursorCalled = false;

        await _relay.FireDisconnected();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_platform.IsOnVirtualScreen, Is.False, "should return to local screen");
            Assert.That(_platform.ShowCursorCalled, Is.True, "should show cursor");
        }
    }

    [Test]
    public async Task RelayDisconnect_WhileOnLocalScreen_NoEffect()
    {
        Assert.That(_platform.IsOnVirtualScreen, Is.False);
        _platform.ShowCursorCalled = false;

        await _relay.FireDisconnected();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_platform.IsOnVirtualScreen, Is.False, "should remain on local screen");
            Assert.That(_platform.ShowCursorCalled, Is.False, "should not call ShowCursor unnecessarily");
        }
    }

    [Test]
    public void Disconnected_BlocksEdgeTransition()
    {
        _relay.Connected = false;

        _platform.HideCursorCalled = false;
        _platform.FireMouseMove(2559, 720);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_platform.HideCursorCalled, Is.False, "should not hide cursor while disconnected");
            Assert.That(_platform.IsOnVirtualScreen, Is.False, "should not enter virtual screen while disconnected");
        }
    }

    [Test]
    public void ReturnToLocal_ShowsCursorImmediately_WithoutExtraMouseMove()
    {
        // enter virtual screen via right edge
        TransitionTestHelper.EnterRemote(_platform);

        _platform.ShowCursorCalled = false;

        // after warp-on-entry to WarpX (1280), nudge left by ≥ NudgeDistance (2) to exit FLIPSIDE
        _platform.FireMouseMove(1276, 720);

        using (Assert.EnterMultipleScope())
        {
            // ShowCursor must have fired immediately — no second mouse move required
            Assert.That(_platform.ShowCursorCalled, Is.True, "ShowCursor should fire immediately on return-to-local, not deferred");
            Assert.That(_platform.IsOnVirtualScreen, Is.False, "should be back on local screen");
        }
    }

    [Test]
    public void Reconnected_AllowsEdgeTransitionAgain()
    {
        // disconnect then reconnect
        _relay.Connected = false;
        _platform.FireMouseMove(2559, 720);
        Assert.That(_platform.IsOnVirtualScreen, Is.False, "pre-condition: blocked while disconnected");

        _relay.Connected = true;
        _platform.HideCursorCalled = false;
        _platform.FireMouseMove(2559, 720);
        Assert.That(_platform.HideCursorCalled, Is.True, "transition should work after reconnect");
    }

    // the disconnect event returns the cursor home, but a crossing in the gap before it lands must still be refused
    [Test]
    public async Task Disconnected_BlocksCrossingBetweenRemotes_AbsolutePath()
    {
        await AssertCrossingWaitsForRelay(p => p.FireMouseMove(p.WarpX + 100, p.WarpY));
    }

    [Test]
    public async Task Disconnected_BlocksCrossingBetweenRemotes_DeltaPath()
    {
        await AssertCrossingWaitsForRelay(p => p.FireMouseDelta(100, 0));
    }

    private async Task AssertCrossingWaitsForRelay(Action<FakePlatform> pushRight)
    {
        var (platform, relay, _) = await Services.Visiting(TransitionTestHelper.CreateService(profile: TransitionTestHelper.ChainConfig), "remote", "remote2");

        relay.Connected = false;
        for (var i = 0; i < 30; i++)
            pushRight(platform);

        // still parked against remote's edge, so the first push after reconnecting is what crosses
        relay.Connected = true;
        relay.Sent.Clear();
        pushRight(platform);

        Assert.That(relay.Sent.Of<EnterScreenMessage>(MessageKind.EnterScreen, "remote2"), Is.Not.Empty,
            "should only cross to remote2 once the relay is back");
    }
}
