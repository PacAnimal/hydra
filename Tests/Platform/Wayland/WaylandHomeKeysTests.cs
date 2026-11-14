using Hydra.Config;
using Hydra.Keyboard;
using Hydra.Platform;
using Hydra.Platform.Linux;
using Hydra.Platform.Linux.Wayland;
using Hydra.Screen;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.Platform.Wayland;

// Which way the master's hotkeys at home come: the keyboards when the key source opens any, binding no shortcut, else
// the portal's shortcuts, saying once how to have the keyboards read, or that readKeyboardsForHotkeys is off
[TestFixture]
public class WaylandHomeKeysSourceTests
{
    private const string Evdev = "Hotkeys at home: read from the keyboards (evdev)";
    private static ScreenRect Main => new("home:0", "home", 0, 0, 1920, 1080, IsLocal: true);

    private FakeCapturePortal _portal = null!;
    private FakeCaptureSession _session = null!;
    private FakeEvdevKeySource _keys = null!;
    private CapturingLog<WaylandInputHandler> _log = null!;
    private WaylandInputHandler _handler = null!;

    [TearDown]
    public async Task TearDown()
    {
        await _handler.DisposeAsync();
        _keys.Dispose();
    }

    // keyboardsOff: no key source handed over, as with readKeyboardsForHotkeys off
    private async Task Start(bool readable, bool keyboardsOff = false)
    {
        _portal = new FakeCapturePortal();
        _session = new FakeCaptureSession(_portal);
        _keys = new FakeEvdevKeySource(readable);
        _log = new CapturingLog<WaylandInputHandler>(LogLevel.Information);
        _handler = new WaylandInputHandler(_session, TransitionTestHelper.TestConfig, null, new StartedTimers(), _log, useKeymaps: false, hyprland: true,
            keys: keyboardsOff ? null : _keys, keyboardsOff: keyboardsOff);
        _handler.CrossableEdgesChanged([new CrossableEdge(Main, new EdgeSpan(Direction.Right, 0, 1080))]);
        await _handler.StartEventTap((_, _) => { }, null, _ => { }, _ => { }, _ => { });
        await _portal.Until("enable", "the first barriers");
        await _handler.BarrierWorkIdle.WaitAsync(Bound);
    }

    [Test]
    public async Task ReadableKeyboards_BindNoShortcut_AndSaySo()
    {
        await Start(readable: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_session.ShortcutsAsked.Count, Is.Zero, "no global shortcut is bound");
            Assert.That(_log.Messages.Count(m => m == Evdev), Is.EqualTo(1));
            Assert.That(_log.Messages, Has.None.Contains("input group"));
            Assert.That(_keys.Starts, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task UnreadableKeyboards_BindTheShortcuts_AndSayOnceHowToReadThem()
    {
        await Start(readable: false);
        await _session.ShortcutsAsked.Next("the shortcuts' bind");

        // a fresh session binds again, deciding nothing anew
        _session.RaiseSessionLost();
        await _session.ShortcutsAsked.Next("the shortcuts' bind on the fresh session");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_log.Messages.Count(m => m.Contains("input group")), Is.EqualTo(1));
            Assert.That(_log.Messages, Has.None.EqualTo(Evdev));
            Assert.That(_keys.Starts, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task KeyboardsOff_BindTheShortcuts_AndSayOnceWhy()
    {
        await Start(readable: true, keyboardsOff: true);
        await _session.ShortcutsAsked.Next("the shortcuts' bind");

        _session.RaiseSessionLost();
        await _session.ShortcutsAsked.Next("the shortcuts' bind on the fresh session");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_log.Messages.Count(m => m.Contains("readKeyboardsForHotkeys is off")), Is.EqualTo(1));
            Assert.That(_log.Messages, Has.None.Contains("input group"));
            Assert.That(_log.Messages, Has.None.EqualTo(Evdev));
            Assert.That(_keys.Starts, Is.Zero);
        }
    }

    [Test]
    public async Task TheKeySource_IsDisposedWithTheHandler()
    {
        await Start(readable: true);

        await _handler.DisposeAsync();

        Assert.That(_keys.Disposals, Is.EqualTo(1));
    }
}

// The keyboards' keys through a real resolver on the compositor's keymap: at home they reach the router as a shortcut's
// chord would, during a visit only the release of a key pressed at home does, once. Self-skips off Linux
[TestFixture]
[Category("Linux")]
public class WaylandHomeKeysTests
{
    private const uint KeyLeftCtrl = 29, KeyLeftAlt = 56, KeyLeftMeta = 125, KeyL = 38;
    private static ScreenRect Main => new("home:0", "home", 0, 0, 1920, 1080, IsLocal: true);
    private static readonly uint[] Chord = [KeyLeftCtrl, KeyLeftAlt, KeyLeftMeta, KeyL];

    private FakeCapturePortal _portal = null!;
    private FakeCaptureSession _session = null!;
    private FakeEvdevKeySource _source = null!;
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
        _portal = new FakeCapturePortal();
        _session = new FakeCaptureSession(_portal, XkbKeymapLookup.KeymapTextFromNames(new LinuxInputConfig.XkbNames("us", "pc105", null, null)));
        _source = new FakeEvdevKeySource(started: true);
        _hits = new NotificationQueue<long>();
        lock (_keys) _keys.Clear();
        _handler = new WaylandInputHandler(_session, TransitionTestHelper.TestConfig, null, new StartedTimers(), NullLogger.Instance, hyprland: true, keys: _source);
        _handler.ReportHitsTo((_, _, hit) => _hits.Push(hit));
        _handler.CrossableEdgesChanged([new CrossableEdge(Main, new EdgeSpan(Direction.Right, 0, 1080))]);
        await _handler.StartEventTap((_, _) => { }, null, e => { lock (_keys) _keys.Add(e); }, _ => { }, _ => { });
        await _portal.Until("enable", "the first barriers");
        await _handler.BarrierWorkIdle.WaitAsync(Bound);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _handler.DisposeAsync();
        _source.Dispose();
    }

    private List<KeyEvent> TakeKeys()
    {
        lock (_keys)
        {
            var taken = _keys.ToList();
            _keys.Clear();
            return taken;
        }
    }

    // what a shortcut's activation and deactivation of the chord make
    private List<KeyEvent> AsAShortcut()
    {
        TakeKeys();
        _handler.PressChordKeys(Chord, down: true);
        _handler.PressChordKeys([.. Chord.Reverse()], down: false);
        return TakeKeys();
    }

    private async Task Visit()
    {
        _portal.Activate(7, 1920, 500);
        await _hits.Next("the barrier hit");
        _handler.IsOnVirtualScreen = true;
    }

    [Test]
    public void AChordAtHome_ReachesTheRouter_AsAShortcutsActivationWould()
    {
        foreach (var key in Chord) _source.Send(key, pressed: true);
        foreach (var key in Chord.Reverse()) _source.Send(key, pressed: false);

        var keys = TakeKeys();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(keys, Is.EqualTo(AsAShortcut()));
            Assert.That(HotkeyBinding.ChordModifiers(keys[3]), Is.EqualTo(KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super));
        }
    }

    [Test]
    public async Task DuringAVisit_TheKeyboardsKeys_AreIgnored()
    {
        await Visit();
        TakeKeys();

        _source.Send(KeyL, pressed: true);
        _source.Send(KeyL, pressed: false);

        Assert.That(TakeKeys(), Is.Empty);
    }

    [Test]
    public async Task AKeyPressedAtHome_AndReleasedDuringAVisit_ReleasesAtHomeOnce()
    {
        _source.Send(KeyLeftCtrl, pressed: true);
        await Visit();
        TakeKeys();

        // EIS's up for it, with no down this capture, then the keyboards' up, twice
        _session.Receiver.Key(KeyLeftCtrl, pressed: false);
        _source.Send(KeyLeftCtrl, pressed: false);
        _source.Send(KeyLeftCtrl, pressed: false);

        var keys = TakeKeys();
        Assert.That(keys, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(keys[0].Type, Is.EqualTo(KeyEventType.KeyUp));
            Assert.That(keys[0].PhysicalKey, Is.EqualTo((int)KeyLeftCtrl));
        }
    }
}
