using Hydra.Config;
using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Platform;
using Hydra.Platform.Linux;
using Hydra.Platform.Linux.Wayland;
using Hydra.Screen;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.Platform.Wayland;

// The master's Wayland input handler against a fake portal and EIS receiver: barrier work never disables a capture, a
// return releases at once, a visit claims the clipboard while its capture is on, a late StayedHome releases only its own hit, and a capture the
// compositor ends during a visit fails it, as does, on Hyprland, a button up with no down during one
[TestFixture]
public class WaylandCaptureTests
{
    private const uint BtnLeft = 0x110, BtnRight = 0x111;
    private static ScreenRect Main => new("home:0", "home", 0, 0, 1920, 1080, IsLocal: true);
    private static readonly CrossableEdge RightEdge = new(Main, new EdgeSpan(Direction.Right, 0, 1080));
    private static readonly CrossableEdge LeftEdge = new(Main, new EdgeSpan(Direction.Left, 0, 1080));

    private StartedTimers _time = null!;
    private FakeCapturePortal _portal = null!;
    private FakeCaptureSession _session = null!;
    private Claims _clipboard = null!;
    private WaylandInputHandler _handler = null!;
    private NotificationQueue<long> _hits = null!;
    private readonly List<MouseButtonEvent> _buttons = [];
    private int _captureFailed;
    private CapturingLog<WaylandInputHandler> _log = null!;

    [SetUp]
    public async Task SetUp()
    {
        _captureFailed = 0;
        await Start(hyprland: false);
    }

    [TearDown]
    public async Task TearDown() => await _handler.DisposeAsync();

    private async Task Start(bool hyprland)
    {
        _time = new StartedTimers();
        _portal = new FakeCapturePortal();
        _session = new FakeCaptureSession(_portal);
        _clipboard = new Claims();
        _hits = new NotificationQueue<long>();
        lock (_buttons) _buttons.Clear();
        _log = new CapturingLog<WaylandInputHandler>(LogLevel.Debug);
        _handler = new WaylandInputHandler(_session, TransitionTestHelper.TestConfig, _clipboard, _time, _log, useKeymaps: false, hyprland: hyprland);
        _handler.CaptureFailed += () => Interlocked.Increment(ref _captureFailed);
        _handler.ReportHitsTo((_, _, hit) => _hits.Push(hit));
        _handler.CrossableEdgesChanged([RightEdge]);
        await _handler.StartEventTap((_, _) => { }, null, _ => { }, b => { lock (_buttons) _buttons.Add(b); }, _ => { });
        await _portal.Until("enable", "the first barriers");
        await _handler.BarrierWorkIdle.WaitAsync(Bound);
    }

    // the fixture's handler replaced by one on a Hyprland master, or not
    private async Task Restart(bool hyprland)
    {
        await _handler.DisposeAsync();
        await Start(hyprland);
    }

    private IReadOnlyList<MouseButtonEvent> Buttons()
    {
        lock (_buttons) return [.. _buttons];
    }

    // the compositor's hit on the right edge's barrier, as the router is told it; its serial. The hit's watchdog is started
    // before the router is told
    private async Task<long> Hit(uint activationId)
    {
        _portal.Activate(activationId, 1920, 500);
        var hit = await _hits.Next($"barrier hit {activationId}");
        Assert.That(await _time.Started.Next("the hit's watchdog"), Is.EqualTo(TimeSpan.FromSeconds(1)));
        return hit;
    }

    // a hit the router entered a visit on
    private async Task Visit(uint activationId)
    {
        await Hit(activationId);
        _handler.IsOnVirtualScreen = true;
    }

    [Test]
    public async Task AReturn_ReleasesAtOnce_AndBarriersAskedForMeanwhile_AreSetAfterIt()
    {
        await Visit(7);
        var mark = _portal.Mark();

        _handler.ReturnTo(new ScreenPoint(1900, 500));
        _handler.CrossableEdgesChanged([RightEdge, LeftEdge]);
        await _portal.Until("enable", "the barriers after the release");

        Assert.That(_portal.CallsSince(mark), Is.EqualTo(["release 7 at 1900,500", "disable", "barriers 2", "enable"]));
    }

    [Test]
    public async Task AVisit_ClaimsTheClipboard_WhileItsCaptureIsOn()
    {
        await Visit(7);

        await _clipboard.Claimed.Next("the visit's claim");
    }

    [Test]
    public async Task ALateStayedHome_DoesNotReleaseANewerHit()
    {
        var first = await Hit(7);
        // the router is slow: the watchdog releases the first hit
        _time.Advance(TimeSpan.FromSeconds(1));
        await _portal.Until("release 7", "the watchdog's release");
        await Hit(8);

        _handler.StayedHome(first);
        _handler.IsOnVirtualScreen = true;

        Assert.That(Volatile.Read(ref _captureFailed), Is.Zero, "the visit entered on the newer hit still has its capture");
    }

    [Test]
    public async Task StayedHome_ReleasesItsOwnHit()
    {
        var hit = await Hit(7);

        _handler.StayedHome(hit);

        await _portal.Until("release 7", "the release");
    }

    [Test]
    public async Task DeactivatedDuringAVisit_RaisesCaptureFailed()
    {
        await Visit(7);

        _portal.RaiseDeactivated(7);

        Assert.That(Volatile.Read(ref _captureFailed), Is.EqualTo(1));
    }

    [Test]
    public async Task SessionLostDuringAVisit_RaisesCaptureFailed()
    {
        await Visit(7);

        _session.RaiseSessionLost();

        Assert.That(Volatile.Read(ref _captureFailed), Is.EqualTo(1));
    }

    [Test]
    public async Task AnUnchangedEdgeReport_SetsNothingOnThePortal()
    {
        var mark = _portal.Mark();

        _handler.CrossableEdgesChanged([new CrossableEdge(Main, new EdgeSpan(Direction.Right, 0, 1080))]);
        await _handler.BarrierWorkIdle.WaitAsync(Bound);

        Assert.That(_portal.CallsSince(mark), Is.Empty);
    }

    // a drag into the barrier: the up of a button that went down at home, the only button event of the visit
    [Test]
    public async Task OnHyprland_AnOrphanButtonUpDuringAVisit_EndsIt()
    {
        await Restart(hyprland: true);
        await Visit(7);
        var mark = _portal.Mark();

        _session.Receiver.Press(BtnLeft, pressed: false);
        await _portal.Until("release 7", "the visit's release");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_portal.CallsSince(mark)[0], Does.StartWith("release 7 at "), "the visit's capture is released");
            Assert.That(Volatile.Read(ref _captureFailed), Is.EqualTo(1), "the router is told to return home");
            Assert.That(_handler.IsOnVirtualScreen, Is.False);
            Assert.That(Buttons(), Is.Empty, "no up the slave had no down for");
        }
    }

    // the drag's up while the router is still deciding on the hit: the capture is released at once, and the entry made
    // on the hit after it fails without blaming a missing barrier
    [Test]
    public async Task OnHyprland_AnOrphanButtonUpBeforeTheEntry_ReleasesIt_AndTheEntryFailsQuietly()
    {
        await Restart(hyprland: true);
        await Hit(7);
        var mark = _portal.Mark();

        _session.Receiver.Press(BtnLeft, pressed: false);
        await _portal.Until("release 7", "the drag's release");
        Assert.That(Volatile.Read(ref _captureFailed), Is.Zero, "no visit to fail yet");

        _handler.IsOnVirtualScreen = true;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_portal.CallsSince(mark)[0], Does.StartWith("release 7 at "), "the hit's capture is released");
            Assert.That(Volatile.Read(ref _captureFailed), Is.EqualTo(1), "the entry finds no capture");
            Assert.That(_log.Messages, Has.None.StartsWith("A visit began with no input capture"), "no blame on the barriers");
            Assert.That(_log.Messages, Does.Contain("A visit began after its capture was released for a drag across the edge"));
            Assert.That(Buttons(), Is.Empty, "no up the slave had no down for");
        }
    }

    // the router refuses that hit: a later entry with no capture under it (a monitor switch) is not the drag's
    [Test]
    public async Task OnHyprland_AnOrphanButtonUpBeforeARefusedHit_LeavesALaterUncapturedEntryWarned()
    {
        await Restart(hyprland: true);
        var hit = await Hit(7);

        _session.Receiver.Press(BtnLeft, pressed: false);
        await _portal.Until("release 7", "the drag's release");
        _handler.StayedHome(hit);
        _handler.IsOnVirtualScreen = true;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Volatile.Read(ref _captureFailed), Is.EqualTo(1), "the entry finds no capture");
            Assert.That(_log.Messages, Has.Some.StartsWith("A visit began with no input capture"));
            Assert.That(_log.Messages, Has.None.StartsWith("A visit began after its capture was released for a drag"));
        }
    }

    [Test]
    public async Task OffHyprland_AnOrphanButtonUpDuringAVisit_IsDropped_AndTheVisitGoesOn()
    {
        await Visit(7);
        var mark = _portal.Mark();

        _session.Receiver.Press(BtnLeft, pressed: false);
        _session.Receiver.Press(BtnRight, pressed: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Buttons(), Is.EqualTo([new MouseButtonEvent(MouseButton.Right, true)]));
            Assert.That(_portal.CallsSince(mark), Is.Empty, "the capture is kept");
            Assert.That(Volatile.Read(ref _captureFailed), Is.Zero);
            Assert.That(_handler.IsOnVirtualScreen, Is.True);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task AButtonPressedDuringAVisit_IsForwardedBothWays(bool hyprland)
    {
        if (hyprland) await Restart(hyprland: true);
        await Visit(7);
        var mark = _portal.Mark();

        _session.Receiver.Press(BtnLeft, pressed: true);
        _session.Receiver.Press(BtnLeft, pressed: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Buttons(), Is.EqualTo([new MouseButtonEvent(MouseButton.Left, true), new MouseButtonEvent(MouseButton.Left, false)]));
            Assert.That(_portal.CallsSince(mark), Is.Empty, "the capture is kept");
            Assert.That(Volatile.Read(ref _captureFailed), Is.Zero);
            Assert.That(_handler.IsOnVirtualScreen, Is.True);
        }
    }

    // the clipboard a visit lends its capture: each claim it is asked for
    private sealed class Claims : ICaptureClipboard
    {
        public NotificationQueue<int> Claimed { get; } = new();
        public void Claim() => Claimed.Push(1);
    }
}

// The handler's keys through a real resolver on the compositor's keymap: a capture ending with a modifier held leaves no
// trace at home. Self-skips off Linux; runs in the test-linux CI job and run-tests.sh linux.
[TestFixture]
[Category("Linux")]
public class WaylandCaptureKeysTests
{
    private const uint KeyLeftCtrl = 29, KeyLeftShift = 42, KeyLeftAlt = 56, KeyLeftMeta = 125, KeyL = 38;
    private static ScreenRect Main => new("home:0", "home", 0, 0, 1920, 1080, IsLocal: true);

    private StartedTimers _time = null!;
    private FakeCapturePortal _portal = null!;
    private FakeCaptureSession _session = null!;
    private WaylandInputHandler _handler = null!;
    private NotificationQueue<long> _hits = null!;
    private readonly List<KeyEvent> _keys = [];

    [OneTimeSetUp]
    public void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Ignore("Requires Linux with libxkbcommon (run ./run-tests.sh linux)");
    }

    [SetUp]
    public async Task SetUp()
    {
        _time = new StartedTimers();
        _portal = new FakeCapturePortal();
        _session = new FakeCaptureSession(_portal, XkbKeymapLookup.KeymapTextFromNames(new LinuxInputConfig.XkbNames("us", "pc105", null, null)));
        _hits = new NotificationQueue<long>();
        lock (_keys) _keys.Clear();
        _handler = new WaylandInputHandler(_session, TransitionTestHelper.TestConfig, null, _time, NullLogger.Instance);
        _handler.ReportHitsTo((_, _, hit) => _hits.Push(hit));
        _handler.CrossableEdgesChanged([new CrossableEdge(Main, new EdgeSpan(Direction.Right, 0, 1080))]);
        await _handler.StartEventTap((_, _) => { }, null, e => { lock (_keys) _keys.Add(e); }, _ => { }, _ => { });
        // the keymap is taken before the barriers are asked for
        await _portal.Until("enable", "the first barriers");
        await _handler.BarrierWorkIdle.WaitAsync(Bound);
    }

    [TearDown]
    public async Task TearDown() => await _handler.DisposeAsync();

    private async Task VisitHoldingShift()
    {
        _portal.Activate(7, 1920, 500);
        await _hits.Next("the barrier hit");
        _handler.IsOnVirtualScreen = true;
        _session.Receiver.Key(KeyLeftShift, pressed: true);
    }

    // the chord's down at home, as a shortcut activation presses it
    private KeyEvent ChordAtHome()
    {
        lock (_keys) _keys.Clear();
        _handler.PressChordKeys([KeyLeftCtrl, KeyLeftAlt, KeyLeftMeta, KeyL], down: true);
        lock (_keys) return _keys[^1];
    }

    [Test]
    public async Task AReturnWithShiftHeld_LeavesNoShiftOnAShortcutAtHome()
    {
        await VisitHoldingShift();

        _handler.ReturnTo(new ScreenPoint(1900, 500));

        Assert.That(HotkeyBinding.ChordModifiers(ChordAtHome()), Is.EqualTo(KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super));
    }

    [Test]
    public async Task ADeactivationWithShiftHeld_LeavesNoShiftOnAShortcutAtHome()
    {
        await VisitHoldingShift();

        _portal.RaiseDeactivated(7);

        Assert.That(HotkeyBinding.ChordModifiers(ChordAtHome()), Is.EqualTo(KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super));
    }
}

// the run's portal session as the input handler sees it, serving the one fake portal and receiver
internal sealed class FakeCaptureSession(FakeCapturePortal portal, string? keymap = null) : ICaptureSession
{
    public FakeCaptureReceiver Receiver { get; } = new(keymap);
    // one item each time the handler asks for the shortcuts portal, to bind the hotkeys at home
    public NotificationQueue<int> ShortcutsAsked { get; } = new();

    public Task<ICapturePortal> InputCapture(CancellationToken cancel) => Task.FromResult<ICapturePortal>(portal);
    Task<ICaptureReceiver> ICaptureSession.Receiver(CancellationToken cancel) => Task.FromResult<ICaptureReceiver>(Receiver);

    public Task<GlobalShortcutsPortal?> Shortcuts(CancellationToken cancel)
    {
        ShortcutsAsked.Push(1);
        return Task.FromResult<GlobalShortcutsPortal?>(null);
    }
    public Task<PortalZoneSet> Zones(CancellationToken cancel) => Task.FromResult(new PortalZoneSet(1, [new PortalZone(0, 0, 1920, 1080)]));

    public event Action? ScreensChanged { add { } remove { } }
    public event Action? SessionLost;

    public void RaiseSessionLost() => SessionLost?.Invoke();
}

// an InputCapture portal recording its calls, in order, as "disable", "barriers <count>", "enable" and
// "release <activation> at <x>,<y>"
internal sealed class FakeCapturePortal : ICapturePortal
{
    private readonly List<string> _calls = [];
    private readonly NotificationQueue<string> _made = new();
    private IReadOnlyList<PortalBarrier> _barriers = [];

    public event Action<uint, double, double, uint>? Activated;
    public event Action<uint>? Deactivated;
    public event Action? Disabled { add { } remove { } }
    public event Action? ZonesChanged { add { } remove { } }

    // how many calls were made so far, for CallsSince
    public int Mark()
    {
        lock (_calls) return _calls.Count;
    }

    public IReadOnlyList<string> CallsSince(int mark)
    {
        lock (_calls) return [.. _calls.Skip(mark)];
    }

    // waits for the next call starting with call, passing over the others
    public async Task Until(string call, string what)
    {
        while (!(await _made.Next(what)).StartsWith(call, StringComparison.Ordinal))
        {
        }
    }

    // a hit on the barrier set last, at (x, y)
    public void Activate(uint activationId, double x, double y)
    {
        uint barrier;
        lock (_calls) barrier = _barriers.Single().Id;
        Activated?.Invoke(activationId, x, y, barrier);
    }

    public void RaiseDeactivated(uint activationId) => Deactivated?.Invoke(activationId);

    public Task<IReadOnlyList<uint>> SetPointerBarriers(IReadOnlyList<PortalBarrier> barriers, uint zoneSet, CancellationToken cancel)
    {
        lock (_calls) _barriers = barriers;
        Record($"barriers {barriers.Count}");
        return Task.FromResult<IReadOnlyList<uint>>([]);
    }

    public Task Enable() => Record("enable");
    public Task Disable() => Record("disable");
    public Task Release(uint activationId, double? cursorX, double? cursorY) => Record($"release {activationId} at {cursorX},{cursorY}");

    private Task Record(string call)
    {
        lock (_calls) _calls.Add(call);
        _made.Push(call);
        return Task.CompletedTask;
    }
}

// the EIS receiver, fed by the test
internal sealed class FakeCaptureReceiver(string? keymap = null) : ICaptureReceiver
{
    public event Action<string?>? KeyboardKeymapChanged { add { } remove { } }
    public event Action<uint, bool>? KeyboardKey;
    public event Action<double, double>? PointerMotion { add { } remove { } }
    public event Action<uint, bool>? Button;
    public event Action<double, double>? ScrollDelta { add { } remove { } }
    public event Action<int, int>? ScrollDiscrete { add { } remove { } }
    public event Action<bool, bool>? ScrollStop { add { } remove { } }
    public event Action? Frame { add { } remove { } }

    public string? KeyboardKeymap => keymap;
    public bool IsDisconnected => false;

    public void Key(uint code, bool pressed) => KeyboardKey?.Invoke(code, pressed);
    public void Press(uint code, bool pressed) => Button?.Invoke(code, pressed);
}

// the kernel's keyboards, fed by the test; Start answers started
internal sealed class FakeEvdevKeySource(bool started) : IEvdevKeySource
{
    private int _starts;
    private int _disposals;

    public event Action<uint, bool>? Key;

    public int Starts => Volatile.Read(ref _starts);
    public int Disposals => Volatile.Read(ref _disposals);

    public bool Start()
    {
        Interlocked.Increment(ref _starts);
        return started;
    }

    public void Send(uint code, bool pressed) => Key?.Invoke(code, pressed);

    public void Dispose() => Interlocked.Increment(ref _disposals);
}
