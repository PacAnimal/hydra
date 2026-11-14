using Hydra.Relay;
using Hydra.Screen;
using Microsoft.Extensions.Time.Testing;
using Tests.Setup;

namespace Tests.Screen;

// motion the send throttle holds back goes out once its interval is up, even when no further input follows
[TestFixture]
public class MotionTrailTests : ServicesFixture
{
    // inside the default 8ms send interval
    private static readonly TimeSpan WithinTheInterval = TimeSpan.FromMilliseconds(1);
    // beyond any send interval, so stepping this far fires an armed trailing send
    private static readonly TimeSpan PastAnyInterval = TimeSpan.FromSeconds(1);

    private FakePlatform _platform = null!;
    private FakeRelay _relay = null!;
    private InputRouter _service = null!;
    private readonly FakeTimeProvider _timers = new();

    [SetUp]
    public async Task SetUp()
    {
        (_platform, _relay, _service) = await Services.Visiting(TransitionTestHelper.CreateService(timeProvider: _timers));
    }

    [Test]
    public async Task AbsoluteMode_TheHeldBackPositionIsSentOnceTheIntervalIsUp()
    {
        _relay.Sent.Clear();
        _timers.Advance(TimeSpan.FromSeconds(1));
        _platform.FireMouseMove(_platform.WarpX + 5, _platform.WarpY);
        _timers.Advance(WithinTheInterval);
        _platform.FireMouseMove(_platform.WarpX + 5, _platform.WarpY);

        var first = _relay.Sent.Of<MouseMoveMessage>(MessageKind.MouseMove);
        Assert.That(first, Has.Count.EqualTo(1), "the second move is inside the interval, so it is held back");

        await PassTheInterval();
        await PassTheInterval();

        var moves = _relay.Sent.Of<MouseMoveMessage>(MessageKind.MouseMove);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(moves, Has.Count.EqualTo(2), "the held-back position goes out exactly once");
            Assert.That(moves[^1].X, Is.EqualTo(first[0].X + 5), "where the cursor stopped, not short of it");
        }
    }

    [Test]
    public async Task RelativeMode_TheHeldBackDeltaIsSentOnceTheIntervalIsUp()
    {
        _platform.PressHotkey('m');
        _relay.Sent.Clear();
        _timers.Advance(TimeSpan.FromSeconds(1));
        _platform.FireMouseDelta(-10, 0);
        _timers.Advance(WithinTheInterval);
        _platform.FireMouseDelta(-10, 0);
        _platform.FireMouseDelta(-5, 0);

        var first = _relay.Sent.Of<MouseMoveDeltaMessage>(MessageKind.MouseMoveDelta);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Has.Count.EqualTo(1), "the later deltas are inside the interval, so they are held back");
            Assert.That(first[0].Dx, Is.EqualTo(-10));
        }

        await PassTheInterval();
        await PassTheInterval();

        var deltas = _relay.Sent.Of<MouseMoveDeltaMessage>(MessageKind.MouseMoveDelta);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deltas, Has.Count.EqualTo(2), "the held-back motion goes out as one delta, exactly once");
            Assert.That(deltas[^1].Dx, Is.EqualTo(-15), "the summed delta of everything held back");
        }
    }

    // the router's clock moves past the interval, firing the armed send; the fence settles what it posted
    private async Task PassTheInterval()
    {
        _timers.Advance(PastAnyInterval);
        await _service.Flush();
    }
}
