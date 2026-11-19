using Hydra.Keyboard;
using Hydra.Relay;
using Microsoft.Extensions.Time.Testing;
using Tests.Setup;

namespace Tests.Screen;

// relative mode forwards every delta and ignores the virtual cursor: no clamp, no edge; the hotkeys leave it
[TestFixture]
public class RelativeMouseTests : ServicesFixture
{
    private FakePlatform _platform = null!;
    private FakeRelay _relay = null!;
    private static readonly TimeSpan PastTheThrottle = TimeSpan.FromMilliseconds(20);

    private readonly FakeTimeProvider _time = new();

    [SetUp]
    public async Task SetUp()
    {
        // the visit enters the remote at its left edge, which leads straight back home
        (_platform, _relay, _) = await Services.Visiting(TransitionTestHelper.CreateService(timeProvider: _time));
    }

    [Test]
    public void ALongRunLeft_IsForwardedInFull_AndNeverLeavesByTheEdge()
    {
        _platform.PressHotkey('m');
        _relay.Sent.Clear();

        // position samples first: each recentres, so the delta run after them starts from the warp point;
        // half-pixel steps, so the remainders must add up rather than be dropped
        for (var i = 0; i < 100; i++)
        {
            _time.Advance(PastTheThrottle);
            _platform.FireMouseMove(_platform.WarpX - 30, _platform.WarpY);
        }
        for (var i = 0; i < 4000; i++)
        {
            _time.Advance(PastTheThrottle);
            _platform.FireMouseDelta(-2.5, 0);
        }

        var deltas = _relay.Sent.Of<MouseMoveDeltaMessage>(MessageKind.MouseMoveDelta, "remote");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deltas.Sum(d => d.Dx), Is.EqualTo(-13_000), "every delta reaches the slave, however far past the edge");
            Assert.That(deltas.Sum(d => d.Dy), Is.Zero);
            Assert.That(_relay.Sent.Of(MessageKind.MouseMove), Is.Empty);
            Assert.That(_relay.Sent.Of(MessageKind.LeaveScreen), Is.Empty);
            Assert.That(_relay.Sent.Of(MessageKind.EnterScreen), Is.Empty);
            Assert.That(_platform.IsOnVirtualScreen, Is.True, "the visit goes on");
        }
    }

    [Test]
    public void TogglingBackToAbsolute_CrossesTheEdgeAgain()
    {
        _platform.PressHotkey('m');
        for (var i = 0; i < 50; i++)
            _platform.FireMouseDelta(-100, 0);
        _platform.PressHotkey('m');
        _relay.Sent.Clear();

        // the virtual cursor is still where relative mode began, at the left edge
        _platform.FireMouseDelta(-10, 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_relay.Sent.Of(MessageKind.LeaveScreen, "remote"), Has.Count.EqualTo(1));
            Assert.That(_platform.IsOnVirtualScreen, Is.False);
        }
    }

    [Test]
    public void TheMonitorSwitch_LeavesARelativeScreen()
    {
        _platform.PressHotkey('m');
        for (var i = 0; i < 50; i++)
            _platform.FireMouseDelta(100, 0);
        _relay.Sent.Clear();

        _platform.PressHotkey(SpecialKey.Left);
        _platform.PressHotkey(SpecialKey.Left, up: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_relay.Sent.Of(MessageKind.LeaveScreen, "remote"), Has.Count.EqualTo(1));
            Assert.That(_platform.IsOnVirtualScreen, Is.False);
        }
    }

    [Test]
    public void AbsoluteMode_StillClampsAtAnEdgeWithNoNeighbour()
    {
        for (var i = 0; i < 50; i++)
        {
            _time.Advance(PastTheThrottle);
            _platform.FireMouseDelta(100, 0);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_relay.Sent.Of<MouseMoveMessage>(MessageKind.MouseMove, "remote").Last().X, Is.EqualTo(2559));
            Assert.That(_relay.Sent.Of(MessageKind.MouseMoveDelta), Is.Empty);
            Assert.That(_platform.IsOnVirtualScreen, Is.True);
        }
    }
}
