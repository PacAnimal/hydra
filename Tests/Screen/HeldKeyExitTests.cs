using Hydra.Keyboard;
using Hydra.Relay;
using Hydra.Screen;
using Microsoft.Extensions.Logging;
using Tests.Setup;

namespace Tests.Screen;

// A key pressed on a remote holds the cursor on that host until its up, as a key held at home keeps it home; moves
// between one machine's own screens stay free, and the monitor switch still leaves.
[TestFixture]
public class HeldKeyExitTests : ServicesFixture
{
    // 50px per move, inside the absolute path's warp-artifact filter; 120 of them cross two monitors
    private const int Steps = 120, Step = 50;
    private const int KeyA = 30, KeyB = 48, AltL = 56, CapsLock = 58;

    private static void Press(FakePlatform platform, int key = AltL) => platform.FireKeyEvent(Key(KeyEventType.KeyDown, key));

    private static void Release(FakePlatform platform, int key = AltL) => platform.FireKeyEvent(Key(KeyEventType.KeyUp, key));

    private static KeyEvent Key(KeyEventType type, int key) => key switch
    {
        AltL => KeyEvent.Special(type, SpecialKey.Alt_L, type == KeyEventType.KeyDown ? KeyModifiers.Alt : KeyModifiers.None) with { PhysicalKey = key },
        CapsLock => KeyEvent.Special(type, SpecialKey.CapsLock, KeyModifiers.CapsLock) with { PhysicalKey = key },
        _ => KeyEvent.Char(type, key == KeyA ? 'a' : 'b', KeyModifiers.None) with { PhysicalKey = key },
    };

    // pushes the cursor sideways (direction -1 or 1) far enough to reach any edge of a screen, on either capture path
    private static void Push(FakePlatform platform, bool delta, int direction)
    {
        for (var i = 0; i < Steps; i++)
        {
            if (delta) platform.FireMouseDelta(direction * Step, 0);
            else platform.FireMouseMove(platform.WarpX + direction * Step, platform.WarpY);
        }
    }

    private static bool Entered(FakeRelay relay, string host) => relay.Sent.Of(MessageKind.EnterScreen, host).Count > 0;

    private static List<string> Enters(FakeRelay relay) => [.. relay.Sent.Of<EnterScreenMessage>(MessageKind.EnterScreen).Select(e => e.Screen)];

    private static int Refusals(CapturingLog<InputRouter> log) => log.Messages.Count(m => m.StartsWith("Edge refused", StringComparison.Ordinal));

    private static FakeScreenDetector TwoLocalScreens() => new()
    {
        Snapshot = new LocalScreenSnapshot(
            [new ScreenRect("home:0", "home", 0, 0, 2560, 1440, IsLocal: true), new ScreenRect("home:1", "home", 2560, 0, 2560, 1440, IsLocal: true)],
            [new ScreenInfoEntry("home:0", 0, 0, 2560, 1440, 1.0m), new ScreenInfoEntry("home:1", 2560, 0, 2560, 1440, 1.0m)]),
    };

    [TestCase(false)]
    [TestCase(true)]
    public async Task KeyPressedOnARemote_HoldsTheCursorThereUntilItsUp(bool delta)
    {
        var bundle = await Services.Visiting(TransitionTestHelper.CreateService());
        Press(bundle.Platform);

        Push(bundle.Platform, delta, -1);
        var whileHeld = bundle.Platform.IsOnVirtualScreen;
        Release(bundle.Platform);
        Push(bundle.Platform, delta, -1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(whileHeld, Is.True, "refused home while the key is held");
            Assert.That(bundle.Platform.IsOnVirtualScreen, Is.False, "home after its up");
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task KeyPressedOnARemote_HoldsTheCursorOnThatHostUntilItsUp(bool delta)
    {
        var bundle = await Services.Visiting(TransitionTestHelper.CreateService(profile: TransitionTestHelper.ChainConfig), ["remote", "remote2"]);
        Press(bundle.Platform);
        bundle.Relay.Sent.Clear();

        Push(bundle.Platform, delta, 1);
        var whileHeld = Entered(bundle.Relay, "remote2");
        Release(bundle.Platform);
        Push(bundle.Platform, delta, 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(whileHeld, Is.False, "refused onto remote2 while the key is held");
            Assert.That(Entered(bundle.Relay, "remote2"), Is.True, "onto remote2 after its up");
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task KeyHeld_CrossingBetweenATwoScreenSlavesScreensIsAllowed(bool delta)
    {
        var bundle = await Services.StartOnline(TransitionTestHelper.CreateService());
        await TransitionTestHelper.AdvertiseScreens(bundle.Relay, "remote", 2);
        TransitionTestHelper.EnterRemote(bundle.Platform);
        Press(bundle.Platform);
        bundle.Relay.Sent.Clear();

        // remote:0 onto remote:1, then back across both to the edge home, which the key still holds
        Push(bundle.Platform, delta, 1);
        var right = Enters(bundle.Relay);
        Push(bundle.Platform, delta, -1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(right, Is.EqualTo(["remote:1"]), "onto the slave's second screen with the key held");
            Assert.That(Enters(bundle.Relay), Is.EqualTo(["remote:1", "remote:0"]), "and back onto its first");
            Assert.That(bundle.Relay.Sent.Of(MessageKind.LeaveScreen), Is.Empty, "never leaves the host");
            Assert.That(bundle.Platform.IsOnVirtualScreen, Is.True, "the edge home is refused");
        }
    }

    [Test]
    public async Task KeyHeldAtHome_CrossingBetweenATwoScreenMastersScreensIsAllowed()
    {
        // the master's own screens are the OS's to cross; only the edge onto the remote is refused, by the entry gate
        var log = new CapturingLog<InputRouter>(LogLevel.Information);
        var bundle = await Services.StartOnline(TransitionTestHelper.CreateService(screens: TwoLocalScreens(), log: log));
        bundle.Platform.AnyKeyHeld = true;

        bundle.Platform.FireMouseMove(2559, 720);
        bundle.Platform.FireMouseMove(2560, 720);
        bundle.Platform.FireMouseMove(4000, 720);
        var between = bundle.Platform.IsOnVirtualScreen;
        var refusedBetween = log.Messages.Count(m => m.Contains("refused", StringComparison.Ordinal));
        bundle.Platform.FireMouseMove(5119, 720);
        var atTheRemotesEdge = bundle.Platform.IsOnVirtualScreen;
        bundle.Platform.AnyKeyHeld = false;
        bundle.Platform.FireMouseMove(5119, 721);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(between, Is.False, "home:0 to home:1 stays home");
            Assert.That(refusedBetween, Is.Zero, "nothing refused between the master's own screens");
            Assert.That(atTheRemotesEdge, Is.False, "the edge onto the remote is refused with the key held");
            Assert.That(bundle.Platform.IsOnVirtualScreen, Is.True, "and crossed after its up, so that edge leads to the remote");
        }
    }

    [TestCase(false, ExpectedResult = false)]
    [TestCase(true, ExpectedResult = true)]
    public async Task<bool> PlatformThatSeesSwallowedKeys_IsAskedWhetherTheKeyIsStillHeld(bool held)
    {
        // macOS and Xorg see a key their tap swallowed, so an up that never arrived cannot hold the cursor
        var bundle = await Services.Visiting(TransitionTestHelper.CreateService());
        Press(bundle.Platform);
        bundle.Platform.KeyHeldByOs = held;

        Push(bundle.Platform, delta: true, -1);

        var stayed = bundle.Platform.IsOnVirtualScreen;
        return stayed;
    }

    [Test]
    public async Task KeyHeld_MonitorSwitchStillChangesHost()
    {
        var bundle = await Services.Visiting(TransitionTestHelper.CreateService(profile: TransitionTestHelper.ChainConfig), ["remote", "remote2"]);
        Press(bundle.Platform, KeyA);
        bundle.Relay.Sent.Clear();

        bundle.Platform.PressHotkey(SpecialKey.Right);
        bundle.Platform.PressHotkey(SpecialKey.Right, up: true);
        var onRemote2 = Entered(bundle.Relay, "remote2");
        Release(bundle.Platform, KeyA);
        var upToRemote2 = bundle.Relay.Sent.Of(MessageKind.KeyEvent, "remote2").Count;
        bundle.Platform.PressHotkey(SpecialKey.Left);
        bundle.Platform.PressHotkey(SpecialKey.Left, up: true);
        Press(bundle.Platform, KeyB);
        bundle.Platform.PressHotkey(SpecialKey.Left);
        bundle.Platform.PressHotkey(SpecialKey.Left, up: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(onRemote2, Is.True, "switched onto remote2 with a key held on remote");
            Assert.That(bundle.Relay.Sent.Of(MessageKind.LeaveScreen, "remote"), Has.Count.EqualTo(2), "remote left, its release-all taking the key");
            Assert.That(upToRemote2, Is.Zero, "the key's up stays off remote2, which never had its down");
            Assert.That(bundle.Platform.IsOnVirtualScreen, Is.False, "switched home with a key held on remote");
        }
    }

    [Test]
    public async Task LockKeyTurnedOnOnARemote_DoesNotHoldTheCursor()
    {
        // a macOS master reports CapsLock's on as its down and sends no up until it goes off
        var bundle = await Services.Visiting(TransitionTestHelper.CreateService());
        Press(bundle.Platform, CapsLock);

        Push(bundle.Platform, delta: true, -1);

        Assert.That(bundle.Platform.IsOnVirtualScreen, Is.False);
    }

    [Test]
    public async Task RefusedEdge_IsLoggedOncePerKeyState()
    {
        var log = new CapturingLog<InputRouter>(LogLevel.Information);
        var bundle = await Services.Visiting(TransitionTestHelper.CreateService(log: log));
        Press(bundle.Platform, KeyA);

        Push(bundle.Platform, delta: true, -1);
        var once = Refusals(log);
        bundle.Platform.FireKeyEvent(Key(KeyEventType.KeyDown, KeyA) with { IsRepeat = true });
        Push(bundle.Platform, delta: true, -1);
        var afterRepeat = Refusals(log);
        Press(bundle.Platform, KeyB);
        Push(bundle.Platform, delta: true, -1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(once, Is.EqualTo(1), "one line however often the edge is hit");
            Assert.That(afterRepeat, Is.EqualTo(1), "a repeat is the same hold");
            Assert.That(Refusals(log), Is.EqualTo(2), "another after a key goes down");
            Assert.That(log.Messages.Last(m => m.StartsWith("Edge refused", StringComparison.Ordinal)), Is.EqualTo("Edge refused: 'a', 'b' held on remote"),
                "names the keys and the host");
        }
    }
}
