using Cathedral.Utils;
using Hydra.Config;
using Hydra.Relay;
using Microsoft.Extensions.Time.Testing;
using Tests.Setup;

namespace Tests.Screen;

[TestFixture]
public class MouseThrottleTests : ServicesFixture
{
    // beyond any mouse-batch flush interval, so stepping a FakeTimeProvider this far fires an armed flush
    private static readonly TimeSpan PastAnyFlush = TimeSpan.FromSeconds(1);

    private FakePlatform _platform = null!;
    private FakeRelay _relay = null!;
    private readonly SteppableTicks _ticks = new();

    [SetUp]
    public async Task SetUp()
    {
        (_platform, _relay, _) = await Services.StartOnline(TransitionTestHelper.CreateService(getTickCount: _ticks.Now));
    }

    [Test]
    public async Task MouseMoves_AreThrottled_ToMaxHz()
    {
        // frozen clock: all 50 events share the same tick so only the first send fires
        var (platform, relay, _) = await Services.Visiting(TransitionTestHelper.CreateService(getTickCount: () => 1000L));
        relay.Sent.Clear();

        var warpX = platform.WarpX;
        var warpY = platform.WarpY;

        for (var i = 0; i < 50; i++)
            platform.FireMouseMove(warpX + 5, warpY);

        var mouseMoves = relay.Sent.Of(MessageKind.MouseMove).Count;
        Assert.That(mouseMoves, Is.LessThan(15),
            $"Expected throttling but got {mouseMoves} MouseMove sends for 50 events");
    }

    [Test]
    public async Task RawMouseBurst_IsBoundedToInFlightAndPendingActorCommands()
    {
        // frozen clock: every sample after the first lands inside one send interval
        var tracker = new HeldActivityTracker();
        var timers = new FakeTimeProvider();
        var (platform, _, service) = await Services.Visiting(TransitionTestHelper.CreateService(
            getTickCount: () => 1000L, activityTracker: tracker, timeProvider: timers));

        platform.AfterFireCallback = null;
        tracker.Hold();
        var before = service.PostedMouseBatchCount;
        try
        {
            // one batch in flight, held by the blocked consumer
            platform.FireMouseMove(platform.WarpX + 2, platform.WarpY + 1);
            timers.Advance(PastAnyFlush);
            await tracker.Held.WaitAsync(Bound);

            for (var i = 0; i < 10_000; i++)
                platform.FireMouseMove(platform.WarpX + 2, platform.WarpY + 1);
            Assert.That(service.PostedMouseBatchCount - before, Is.EqualTo(1),
                "the whole burst coalesces into the batch still waiting out its send interval");

            timers.Advance(PastAnyFlush);
            Assert.That(service.PostedMouseBatchCount - before, Is.EqualTo(2),
                "one in-flight batch plus one pending batch should absorb the entire burst");
        }
        finally
        {
            // a blocked consumer would hang the teardown's stop
            tracker.Release();
        }
        await service.Flush();
    }

    [Test]
    public async Task RawSamplesWithinOneInterval_PostASingleActorCommand()
    {
        // frozen clock: the interval never elapses, so nothing but the first sample may post
        var timers = new FakeTimeProvider();
        var (platform, _, service) = await Services.Visiting(TransitionTestHelper.CreateService(
            getTickCount: () => 1000L, timeProvider: timers));

        platform.AfterFireCallback = null;
        var before = service.PostedMouseBatchCount;
        for (var i = 0; i < 500; i++)
            platform.FireMouseMove(platform.WarpX + 1, platform.WarpY);

        Assert.That(service.PostedMouseBatchCount - before, Is.Zero,
            "samples inside one send interval are coalesced, not posted one command apiece");
    }

    [Test]
    public async Task ConfiguredMaxMouseHz_DecidesBothTheSendRateAndTheBatchInterval()
    {
        // 50 Hz is a 20ms interval against the default's 8 — a configured value that never reached
        // the router would leave both of those at 8 and the counts identical
        var atDefault = await SendsOverSixtyMillisecondsOfMotion(maxMouseHz: null);
        var atFifty = await SendsOverSixtyMillisecondsOfMotion(maxMouseHz: 50);

        using (Assert.EnterMultipleScope())
        {
            // within one, because whether the window's first send lands inside it is a fencepost and
            // not the property; the ratio between the two is
            Assert.That(atDefault, Is.EqualTo(60 / (1000 / HydraProfile.DefaultMaxMouseHz)).Within(1), "the default sends about once per 8ms");
            Assert.That(atFifty, Is.EqualTo(60 / 20).Within(1), "50 Hz sends about once per 20ms");
            Assert.That(atDefault, Is.GreaterThan(atFifty * 2), "a configured rate that never reached the router would leave these equal");
        }
    }

    private async Task<int> SendsOverSixtyMillisecondsOfMotion(int? maxMouseHz)
    {
        var now = new Boxed<long>(1000L);
        var (platform, relay, _) = await Services.Visiting(TransitionTestHelper.CreateService(
            getTickCount: () => now.Value, profile: TransitionTestHelper.ProfileWith(maxMouseHz)));
        relay.Sent.Clear();

        // one sample per millisecond, so the send count is exactly the number of intervals elapsed
        for (var ms = 0; ms < 60; ms++)
        {
            now.Value++;
            platform.FireMouseMove(platform.WarpX + (ms % 2 == 0 ? 1 : 2), platform.WarpY);
        }

        return relay.Sent.Of(MessageKind.MouseMove).Count;
    }

    [Test]
    public async Task PositionCapture_RecentresOnEverySample()
    {
        // Mac/Windows read the cursor as an absolute position while it is frozen on the virtual
        // screen — waiting for drift to cross a dead zone before recentring gives the OS's own
        // tracked position room to reach the real screen edge during perfectly ordinary movement,
        // and once it does, movement in that direction stops registering for the rest of the visit.
        // So unlike the delta-reporting path below, this one recentres on every sample, however
        // small — sample batching upstream already keeps the resulting warp rate to at most
        // MaxMouseHz, so this is not the ~900/s a raw mouse would otherwise produce.
        var (platform, _, _) = await Services.Visiting(TransitionTestHelper.CreateService());

        var warpX = platform.WarpX;
        var warpY = platform.WarpY;
        var before = platform.WarpCount;

        for (var i = 1; i <= 5; i++)
            platform.FireMouseMove(warpX + i, warpY);
        Assert.That(platform.WarpCount, Is.EqualTo(before + 5),
            "every processed position sample recentres, however small the drift");
    }

    [Test]
    public async Task DeltaCapture_RecentresOnlyAfterItDriftsOutOfTheDeadZone()
    {
        // Linux evdev/Xorg's delta is XI_RawMotion: a raw hardware-relative stream with no notion of
        // an on-screen position, so it can never run into a real screen edge no matter how long it
        // goes between warps — recentring less often than every sample costs nothing here but an X
        // warp + socket flush.
        var (platform, _, _) = await Services.Visiting(TransitionTestHelper.CreateService());

        // dead zone is 10% of the half-screen, so 128px on a 2560-wide local screen
        var before = platform.WarpCount;

        for (var i = 0; i < 100; i++)
            platform.FireMouseDelta(1, 0);
        Assert.That(platform.WarpCount, Is.EqualTo(before),
            "a cursor still inside the dead zone costs no warp at all");

        for (var i = 0; i < 28; i++)
            platform.FireMouseDelta(1, 0);
        Assert.That(platform.WarpCount, Is.EqualTo(before + 1),
            "crossing the dead zone recentres exactly once");
    }

    [Test]
    public async Task WindowsDeltaCapture_TheRouterNeverRecentres()
    {
        // WindowsInputHandler recentres per sample itself; a router warp on top would be an extra reset jump
        var (platform, relay, _) = await Services.Start(TransitionTestHelper.CreateService());
        platform.RecentresItself = true;
        await TransitionTestHelper.BringRemoteOnline(relay);
        TransitionTestHelper.EnterRemote(platform);

        var before = platform.WarpCount;

        // well past the 128px dead zone a router-side recentre would warp at
        for (var i = 0; i < 200; i++)
            platform.FireMouseDelta(1, 0);
        Assert.That(platform.WarpCount, Is.EqualTo(before),
            "the router must not recentre Windows' delta capture — WindowsInputHandler already does, per sample, on the hook thread");
    }

    // a stale absolute sample still reaching the router on Windows must not warp from the router thread either
    [Test]
    public async Task WindowsAbsoluteSample_TheRouterNeverRecentres()
    {
        var (platform, relay, _) = await Services.Start(TransitionTestHelper.CreateService());
        platform.RecentresItself = true;
        await TransitionTestHelper.BringRemoteOnline(relay);
        TransitionTestHelper.EnterRemote(platform);

        var before = platform.WarpCount;
        for (var i = 0; i < 10; i++)
            platform.FireMouseMove(platform.WarpX + 5, platform.WarpY);
        Assert.That(platform.WarpCount, Is.EqualTo(before));
    }

    [Test]
    public void RelativeMouseToggle_Hotkey_SendsDeltaMessages()
    {
        TransitionTestHelper.EnterRemote(_platform);

        // toggle to relative mode with Ctrl+Alt+Super+M
        _platform.PressHotkey('m');

        _relay.Sent.Clear();
        var warpX = _platform.WarpX;
        var warpY = _platform.WarpY;

        // wait past the throttle interval to ensure a send happens
        _ticks.Advance(20);
        _platform.FireMouseMove(warpX + 10, warpY + 5);
        _ticks.Advance(20);
        _platform.FireMouseMove(warpX + 5, warpY);

        var deltaMessages = _relay.Sent.Of(MessageKind.MouseMoveDelta);
        var absoluteMessages = _relay.Sent.Of(MessageKind.MouseMove);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deltaMessages, Is.Not.Empty, "expected MouseMoveDelta messages in relative mode");
            Assert.That(absoluteMessages, Is.Empty, "expected no MouseMove messages in relative mode");
        }
    }

    [Test]
    public void RelativeMouseToggle_TogglesBackToAbsolute()
    {
        TransitionTestHelper.EnterRemote(_platform);

        // toggle to relative
        _platform.PressHotkey('m');

        // toggle back to absolute
        _platform.PressHotkey('m');

        _relay.Sent.Clear();
        var warpX = _platform.WarpX;
        var warpY = _platform.WarpY;

        _ticks.Advance(20);
        _platform.FireMouseMove(warpX + 10, warpY);

        var absoluteMessages = _relay.Sent.Of(MessageKind.MouseMove);
        Assert.That(absoluteMessages, Is.Not.Empty, "expected MouseMove (absolute) after toggling back");
    }

    [Test]
    public void RelativeMouseToggle_WhenNotOnVirtualScreen_DoesNothing()
    {
        Assert.That(_platform.IsOnVirtualScreen, Is.False);

        // toggle while not on virtual screen — should silently do nothing
        Assert.DoesNotThrow(() =>
            _platform.PressHotkey('m'));
    }
}
