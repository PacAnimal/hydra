using System.Text.Json;
using Cathedral.Utils;
using Hydra.Config;
using Hydra.Keyboard;
using Hydra.Relay;
using Hydra.Screen;
using Tests.Setup;

namespace Tests.Screen;

[TestFixture]
public class MouseThrottleTests
{
    private const int MouseSendIntervalMs = 1000 / HydraProfile.DefaultMaxMouseHz;

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

    [Test]
    public async Task MouseMoves_AreThrottled_ToMaxHz()
    {
        // frozen clock: all 50 events share the same tick so only the first send fires
        var (platform, relay, service) = TransitionTestHelper.CreateService(getTickCount: () => 1000L);
        await service.StartAsync(CancellationToken.None);
        await BringRemoteOnline(relay);

        platform.FireMouseMove(2559, 720);
        Assert.That(platform.IsOnVirtualScreen, Is.True);
        relay.Sent.Clear();

        var warpX = platform.WarpX;
        var warpY = platform.WarpY;

        for (var i = 0; i < 50; i++)
            platform.FireMouseMove(warpX + 5, warpY);

        var mouseMoves = relay.Sent.Count(s => s.Kind == MessageKind.MouseMove);
        Assert.That(mouseMoves, Is.LessThan(15),
            $"Expected throttling but got {mouseMoves} MouseMove sends for 50 events");

        await service.StopAsync(CancellationToken.None);
        await platform.DisposeAsync();
    }

    [Test]
    public async Task RawMouseBurst_IsBoundedToInFlightAndPendingActorCommands()
    {
        // frozen clock: every sample after the first lands inside one send interval
        var tracker = new BlockingActivityTracker();
        var timers = new ManualTimerProvider();
        var (platform, relay, service) = TransitionTestHelper.CreateService(
            getTickCount: () => 1000L, activityTracker: tracker, timeProvider: timers);
        await service.StartAsync(CancellationToken.None);
        await BringRemoteOnline(relay);
        platform.FireMouseMove(2559, 720);
        Assert.That(platform.IsOnVirtualScreen, Is.True);

        platform.AfterFireCallback = null;
        tracker.BlockNext();
        var before = service.PostedMouseBatchCount;

        // one batch in flight, held by the blocked consumer
        platform.FireMouseMove(platform.WarpX + 2, platform.WarpY + 1);
        timers.FireAll();
        await tracker.WaitUntilBlocked();

        for (var i = 0; i < 10_000; i++)
            platform.FireMouseMove(platform.WarpX + 2, platform.WarpY + 1);
        Assert.That(service.PostedMouseBatchCount - before, Is.EqualTo(1),
            "the whole burst coalesces into the batch still waiting out its send interval");

        timers.FireAll();
        Assert.That(service.PostedMouseBatchCount - before, Is.EqualTo(2),
            "one in-flight batch plus one pending batch should absorb the entire burst");
        tracker.Release();
        await service.FlushAsync();
        await service.StopAsync(CancellationToken.None);
        await platform.DisposeAsync();
    }

    [Test]
    public async Task RawSamplesWithinOneInterval_PostASingleActorCommand()
    {
        // frozen clock: the interval never elapses, so nothing but the first sample may post
        var timers = new ManualTimerProvider();
        var (platform, relay, service) = TransitionTestHelper.CreateService(
            getTickCount: () => 1000L, timeProvider: timers);
        await service.StartAsync(CancellationToken.None);
        await BringRemoteOnline(relay);
        platform.FireMouseMove(2559, 720);
        Assert.That(platform.IsOnVirtualScreen, Is.True);

        platform.AfterFireCallback = null;
        var before = service.PostedMouseBatchCount;
        for (var i = 0; i < 500; i++)
            platform.FireMouseMove(platform.WarpX + 1, platform.WarpY);

        Assert.That(service.PostedMouseBatchCount - before, Is.Zero,
            "samples inside one send interval are coalesced, not posted one command apiece");

        await service.StopAsync(CancellationToken.None);
        await platform.DisposeAsync();
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

    private static async Task<int> SendsOverSixtyMillisecondsOfMotion(int? maxMouseHz)
    {
        var now = new Boxed<long>(1000L);
        var (platform, relay, service) = TransitionTestHelper.CreateService(
            getTickCount: () => now.Value, profile: TransitionTestHelper.ProfileWith(maxMouseHz));
        await service.StartAsync(CancellationToken.None);
        await BringRemoteOnline(relay);
        platform.FireMouseMove(2559, 720);
        relay.Sent.Clear();

        // one sample per millisecond, so the send count is exactly the number of intervals elapsed
        for (var ms = 0; ms < 60; ms++)
        {
            now.Value++;
            platform.FireMouseMove(platform.WarpX + (ms % 2 == 0 ? 1 : 2), platform.WarpY);
        }

        var sends = relay.Sent.Count(s => s.Kind == MessageKind.MouseMove);
        await service.StopAsync(CancellationToken.None);
        await platform.DisposeAsync();
        return sends;
    }

    [Test]
    public async Task MovementPastTheLastSample_SurvivesTheRecentringWarp()
    {
        // The platform reports the cursor 20px further along than the sample that triggers the warp
        // — movement the warp would discard, since the next sample measures from the warp point.
        // A platform that cannot answer is the control: there the 20px is simply lost.
        var withResidual = await VirtualXAfterOneRecentringSample(reportedOvershoot: 20);
        var withoutResidual = await VirtualXAfterOneRecentringSample(reportedOvershoot: null);

        Assert.That(withResidual - withoutResidual, Is.EqualTo(20),
            "movement past the last sample is read before the warp and folded in, not discarded");
    }

    private static async Task<int> VirtualXAfterOneRecentringSample(int? reportedOvershoot)
    {
        var now = new Boxed<long>(1000L);
        var (platform, relay, service) = TransitionTestHelper.CreateService(getTickCount: () => now.Value);
        await service.StartAsync(CancellationToken.None);
        await BringRemoteOnline(relay);
        platform.FireMouseMove(2559, 720);

        var warpX = platform.WarpX;
        var warpY = platform.WarpY;

        // a position-reporting platform recentres on every sample, so this alone triggers the warp
        platform.CursorPosition = reportedOvershoot is { } overshoot ? (warpX + 5 + overshoot, warpY) : null;
        platform.FireMouseMove(warpX + 5, warpY);

        now.Value += MouseSendIntervalMs;
        platform.FireMouseMove(warpX + 1, warpY);    // carries the accumulated position out on a send

        var json = relay.Sent.Last(s => s.Kind == MessageKind.MouseMove).Json;
        var message = JsonSerializer.Deserialize<MouseMoveMessage>(json, Cathedral.Config.SaneJson.Options)!;

        await service.StopAsync(CancellationToken.None);
        await platform.DisposeAsync();
        return message.X;
    }

    [Test]
    public async Task WarpThatFailsToLand_DoesNotDistortFurtherMovement()
    {
        // A warp that silently fails to move the physical cursor (e.g. Windows SetCursorPos while
        // the hook thread isn't on the input desktop) must not corrupt the drift reference: anchoring
        // it to the intended target regardless measures every later sample's delta against a point
        // the cursor never reached — a phantom jump at best, a dead cursor at worst.
        var succeeded = await VirtualXAfterOneRecentringSampleWithWarp(warpSucceeds: true);
        var failed = await VirtualXAfterOneRecentringSampleWithWarp(warpSucceeds: false);

        Assert.That(failed, Is.EqualTo(succeeded),
            "the same real movement must land at the same place whether or not the warp took effect");
    }

    private static async Task<int> VirtualXAfterOneRecentringSampleWithWarp(bool warpSucceeds)
    {
        var now = new Boxed<long>(1000L);
        var (platform, relay, service) = TransitionTestHelper.CreateService(getTickCount: () => now.Value);
        await service.StartAsync(CancellationToken.None);
        await BringRemoteOnline(relay);
        platform.FireMouseMove(2559, 720);

        var warpX = platform.WarpX;
        var warpY = platform.WarpY;

        // the physical cursor is really at warpX+5 (matching the sample below), and the resulting
        // warp either lands (CursorPosition follows it to warpX) or silently fails (stays put)
        platform.CursorPosition = (warpX + 5, warpY);
        platform.WarpSucceeds = warpSucceeds;
        platform.FireMouseMove(warpX + 5, warpY);
        platform.WarpSucceeds = true;

        // real further movement, reported (as a real hook would) relative to wherever the physical
        // cursor actually is now — not to wherever our own bookkeeping assumes it landed
        var actualBase = warpSucceeds ? warpX : warpX + 5;
        now.Value += MouseSendIntervalMs;
        platform.FireMouseMove(actualBase + 3, warpY);

        var json = relay.Sent.Last(s => s.Kind == MessageKind.MouseMove).Json;
        var message = JsonSerializer.Deserialize<MouseMoveMessage>(json, Cathedral.Config.SaneJson.Options)!;

        await service.StopAsync(CancellationToken.None);
        await platform.DisposeAsync();
        return message.X;
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
        var (platform, relay, service) = TransitionTestHelper.CreateService();
        await service.StartAsync(CancellationToken.None);
        await BringRemoteOnline(relay);
        platform.FireMouseMove(2559, 720);
        Assert.That(platform.IsOnVirtualScreen, Is.True);

        var warpX = platform.WarpX;
        var warpY = platform.WarpY;
        var before = platform.WarpCount;

        for (var i = 1; i <= 5; i++)
            platform.FireMouseMove(warpX + i, warpY);
        Assert.That(platform.WarpCount, Is.EqualTo(before + 5),
            "every processed position sample recentres, however small the drift");

        await service.StopAsync(CancellationToken.None);
        await platform.DisposeAsync();
    }

    [Test]
    public async Task DeltaCapture_RecentresOnlyAfterItDriftsOutOfTheDeadZone()
    {
        // Unlike the absolute-position path above, a delta-reporting surface (Linux evdev/Xorg)
        // never reads the cursor as a position, so a warp neither consumes nor duplicates one of its
        // samples — recentring less often than every sample costs nothing but an X warp + socket
        // flush, and this dead-zone behaviour is unchanged.
        var (platform, relay, service) = TransitionTestHelper.CreateService();
        await service.StartAsync(CancellationToken.None);
        await BringRemoteOnline(relay);
        platform.FireMouseMove(2559, 720);
        Assert.That(platform.IsOnVirtualScreen, Is.True);

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

        await service.StopAsync(CancellationToken.None);
        await platform.DisposeAsync();
    }

    [Test]
    public void RelativeMouseToggle_Hotkey_SendsDeltaMessages()
    {
        // enter virtual screen
        _platform.FireMouseMove(2559, 720);
        Assert.That(_platform.IsOnVirtualScreen, Is.True);

        // toggle to relative mode with Ctrl+Alt+Super+M
        _platform.FireKeyEvent(KeyEvent.Char(KeyEventType.KeyDown, 'm',
            KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super));

        _relay.Sent.Clear();
        var warpX = _platform.WarpX;
        var warpY = _platform.WarpY;

        // wait past the throttle interval to ensure a send happens
        Thread.Sleep(20);
        _platform.FireMouseMove(warpX + 10, warpY + 5);
        Thread.Sleep(20);
        _platform.FireMouseMove(warpX + 5, warpY);

        var deltaMessages = _relay.Sent.Where(s => s.Kind == MessageKind.MouseMoveDelta).ToList();
        var absoluteMessages = _relay.Sent.Where(s => s.Kind == MessageKind.MouseMove).ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deltaMessages, Is.Not.Empty, "expected MouseMoveDelta messages in relative mode");
            Assert.That(absoluteMessages, Is.Empty, "expected no MouseMove messages in relative mode");
        }
    }

    [Test]
    public void RelativeMouseToggle_TogglesBackToAbsolute()
    {
        // enter virtual screen
        _platform.FireMouseMove(2559, 720);
        Assert.That(_platform.IsOnVirtualScreen, Is.True);

        // toggle to relative
        _platform.FireKeyEvent(KeyEvent.Char(KeyEventType.KeyDown, 'm',
            KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super));

        // toggle back to absolute
        _platform.FireKeyEvent(KeyEvent.Char(KeyEventType.KeyDown, 'm',
            KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super));

        _relay.Sent.Clear();
        var warpX = _platform.WarpX;
        var warpY = _platform.WarpY;

        Thread.Sleep(20);
        _platform.FireMouseMove(warpX + 10, warpY);

        var absoluteMessages = _relay.Sent.Where(s => s.Kind == MessageKind.MouseMove).ToList();
        Assert.That(absoluteMessages, Is.Not.Empty, "expected MouseMove (absolute) after toggling back");
    }

    [Test]
    public void RelativeMouseToggle_WhenNotOnVirtualScreen_DoesNothing()
    {
        Assert.That(_platform.IsOnVirtualScreen, Is.False);

        // toggle while not on virtual screen — should silently do nothing
        Assert.DoesNotThrow(() =>
            _platform.FireKeyEvent(KeyEvent.Char(KeyEventType.KeyDown, 'm',
                KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super)));
    }

    [Test]
    public void KeyDown_ForwardsRepeatPreference_NotMarkedRepeat()
    {
        // enter virtual screen
        _platform.FireMouseMove(2559, 720);
        _relay.Sent.Clear();

        _platform.FireKeyEvent(KeyEvent.Char(KeyEventType.KeyDown, 'w', KeyModifiers.None));

        var keySends = _relay.Sent.Where(s => s.Kind == MessageKind.KeyEvent).ToList();
        Assert.That(keySends, Has.Count.GreaterThanOrEqualTo(1));

        var msg = JsonSerializer.Deserialize<KeyEventMessage>(keySends[0].Json, Cathedral.Config.SaneJson.Options);
        Assert.That(msg, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(msg!.IsRepeat, Is.False, "an initial press is not a repeat");
            Assert.That(msg.UnicodeKeyRepeat, Is.True, "default config enables unicode key repeat");
        }
    }

    [Test]
    public void RepeatKeyEvent_ForwardedMarkedRepeat()
    {
        // enter virtual screen
        _platform.FireMouseMove(2559, 720);
        _relay.Sent.Clear();

        // an OS auto-repeat is re-resolved on the master and surfaces as a KeyEvent flagged IsRepeat
        _platform.FireKeyEvent(KeyEvent.Char(KeyEventType.KeyDown, 'w', KeyModifiers.None) with { IsRepeat = true });

        var keySends = _relay.Sent.Where(s => s.Kind == MessageKind.KeyEvent).ToList();
        Assert.That(keySends, Has.Count.GreaterThanOrEqualTo(1));

        var msg = JsonSerializer.Deserialize<KeyEventMessage>(keySends[0].Json, Cathedral.Config.SaneJson.Options);
        Assert.That(msg, Is.Not.Null);
        Assert.That(msg!.IsRepeat, Is.True, "a repeat key event is forwarded marked as a repeat");
    }

    // -- helpers --

    private static TestServiceBundle CreateService() =>
        TransitionTestHelper.CreateService();

    private static Task BringRemoteOnline(FakeRelay relay) =>
        TransitionTestHelper.BringRemoteOnline(relay);

    private sealed class BlockingActivityTracker : IActivityTracker
    {
        private TaskCompletionSource? _blocked;
        private TaskCompletionSource? _release;

        public long MsSinceLocalActivity => 0;

        public void BlockNext()
        {
            _blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Task WaitUntilBlocked() => _blocked!.Task.WaitAsync(TimeSpan.FromSeconds(3));
        public void Release() => _release!.TrySetResult();

        public async ValueTask LocalActivity()
        {
            if (_blocked == null || _release == null) return;
            _blocked.TrySetResult();
            await _release.Task;
            _blocked = null;
            _release = null;
        }

        public ValueTask RemoteActivity(string sourcePeer) => ValueTask.CompletedTask;
        public ValueTask IncomingPing() => ValueTask.CompletedTask;
    }
}
