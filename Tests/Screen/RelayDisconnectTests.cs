using Hydra.Screen;
using Tests.Setup;

namespace Tests.Screen;

[TestFixture]
public class RelayDisconnectTests
{
    private FakePlatform _platform = null!;
    private FakeRelay _relay = null!;
    private InputRouter _service = null!;

    [SetUp]
    public async Task SetUp()
    {
        (_platform, _relay, _service) = TransitionTestHelper.CreateService();
        await _service.StartAsync(CancellationToken.None);
        await TransitionTestHelper.BringRemoteOnline(_relay);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _service.StopAsync(CancellationToken.None);
        _platform.Dispose();
    }

    [Test]
    public async Task RelayDisconnect_WhileOnVirtualScreen_SnapsBackAndShowsCursor()
    {
        // enter virtual screen
        _platform.FireMouseMove(2559, 720);
        Assert.That(_platform.IsOnVirtualScreen, Is.True, "should be on virtual screen");

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
        _relay.IsConnected = false;

        _platform.HideCursorCalled = false;
        _platform.FireMouseMove(2559, 720);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_platform.HideCursorCalled, Is.False, "should not hide cursor while disconnected");
            Assert.That(_platform.IsOnVirtualScreen, Is.False, "should not enter virtual screen while disconnected");
        }
    }

    [Test]
    public void Reconnected_AllowsEdgeTransitionAgain()
    {
        // disconnect then reconnect
        _relay.IsConnected = false;
        _platform.FireMouseMove(2559, 720);
        Assert.That(_platform.IsOnVirtualScreen, Is.False, "pre-condition: blocked while disconnected");

        _relay.IsConnected = true;
        _platform.HideCursorCalled = false;
        _platform.FireMouseMove(2559, 720);
        Assert.That(_platform.HideCursorCalled, Is.True, "transition should work after reconnect");
    }
}
