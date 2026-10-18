using Hydra.Config;
using Hydra.Mouse;
using Hydra.Relay;
using Hydra.Screen;
using Microsoft.Extensions.Logging;
using Tests.Setup;

namespace Tests.Screen;

// A button pressed on a remote holds the cursor on that host until its up, so the up reaches the host that has its
// down; whatever ends a visit regardless releases the buttons pressed there first.
[TestFixture]
public class HeldButtonTests : ServicesFixture
{
    // 50px per move, inside the absolute path's warp-artifact filter; 120 of them cross two monitors
    private const int Steps = 120, Step = 50;

    private Task<TestServiceBundle> Started(IHydraProfile? profile = null, ILogger<InputRouter>? log = null) =>
        Services.Visiting(TransitionTestHelper.CreateService(profile: profile, log: log), profile is null ? [] : ["remote", "remote2"]);

    private static void Press(FakePlatform platform, MouseButton button = MouseButton.Left) =>
        platform.FireMouseButton(new MouseButtonEvent(button, true));

    private static void Release(FakePlatform platform, MouseButton button = MouseButton.Left) =>
        platform.FireMouseButton(new MouseButtonEvent(button, false));

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

    private static List<MouseButtonMessage> Buttons(FakeRelay relay, string host) => relay.Sent.Of<MouseButtonMessage>(MessageKind.MouseButton, host);

    // the ups sent to a host the cursor left, and whether every one of them went before its LeaveScreen
    private static void AssertReleasedBeforeLeaving(FakeRelay relay, string host, MouseButton button)
    {
        var sent = relay.Sent.Snapshot().Where(s => s.Targets.Contains(host)).ToList();
        var up = sent.FindIndex(s => s.Kind == MessageKind.MouseButton);
        var leave = sent.FindIndex(s => s.Kind == MessageKind.LeaveScreen);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Buttons(relay, host), Is.EqualTo([new MouseButtonMessage(button, false)]), "the held button is released where it went down");
            Assert.That(up, Is.LessThan(leave), "released before the host is told to leave");
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ButtonPressedOnARemote_HoldsTheCursorThereUntilItsUp(bool delta)
    {
        // a Windows master swallows the button in its hook, so the OS never sees it held
        var bundle = await Started();
        Press(bundle.Platform);

        Push(bundle.Platform, delta, -1);
        var whileHeld = bundle.Platform.IsOnVirtualScreen;
        Release(bundle.Platform);
        Push(bundle.Platform, delta, -1);

        var afterUp = bundle.Platform.IsOnVirtualScreen;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(whileHeld, Is.True, "refused home while the button is held");
            Assert.That(afterUp, Is.False, "home after its up");
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ButtonPressedOnARemote_HoldsTheCursorOnThatHostUntilItsUp(bool delta)
    {
        var bundle = await Started(TransitionTestHelper.ChainConfig);
        Press(bundle.Platform);
        bundle.Relay.Sent.Clear();

        Push(bundle.Platform, delta, 1);
        var whileHeld = Entered(bundle.Relay, "remote2");
        Release(bundle.Platform);
        Push(bundle.Platform, delta, 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(whileHeld, Is.False, "refused onto remote2 while the button is held");
            Assert.That(Entered(bundle.Relay, "remote2"), Is.True, "onto remote2 after its up");
        }
    }

    [Test]
    public async Task ButtonHeld_CrossingBetweenOneHostsOwnMonitorsIsAllowed()
    {
        // remote's right edge wraps to its own left monitor: a crossing through the layout that stays on the host
        var profile = TransitionTestHelper.Profile("home", new HydraConfig
        {
            Mode = Mode.Master,
            Hosts =
            [
                new HostConfig { Name = "home", Neighbours = [new NeighbourConfig { Direction = Direction.Right, Name = "remote", Mirror = false }] },
                new HostConfig
                {
                    Name = "remote",
                    Neighbours =
                    [
                        new NeighbourConfig { Direction = Direction.Left, Name = "home", Mirror = false },
                        new NeighbourConfig { Direction = Direction.Right, Name = "remote", SourceScreen = "remote:1", DestScreen = "remote:0", Mirror = false },
                    ],
                },
            ],
        });
        var bundle = await Services.Start(TransitionTestHelper.CreateService(profile: profile));
        await bundle.Relay.FirePeersChanged("remote");
        await TransitionTestHelper.AdvertiseScreens(bundle.Relay, "remote", 2);
        TransitionTestHelper.EnterRemote(bundle.Platform);
        Press(bundle.Platform);
        bundle.Relay.Sent.Clear();

        // remote:0, then remote:1, then through remote:1's right edge back onto remote:0
        Push(bundle.Platform, delta: true, 1);

        var enters = bundle.Relay.Sent.Of<EnterScreenMessage>(MessageKind.EnterScreen).Select(e => e.Screen);
        var left = bundle.Relay.Sent.Of(MessageKind.LeaveScreen).Count > 0;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(enters, Is.EqualTo(["remote:1", "remote:0"]), "wraps onto remote:0 with the button held");
            Assert.That(left, Is.False, "never leaves the host");
        }
    }

    [TestCase(false, ExpectedResult = false)]
    [TestCase(true, ExpectedResult = true)]
    public async Task<bool> PlatformThatSeesSwallowedButtons_IsAskedWhetherTheButtonIsStillHeld(bool held)
    {
        // macOS and Xorg see a button their tap swallowed, so an up that never arrived cannot hold the cursor
        var bundle = await Started();
        bundle.Platform.SeesSwallowedButtons = true;
        Press(bundle.Platform);
        bundle.Platform.AnyMouseButtonHeld = held;

        Push(bundle.Platform, delta: true, -1);

        var stayed = bundle.Platform.IsOnVirtualScreen;
        return stayed;
    }

    [Test]
    public async Task AButtonThePlatformCannotSee_HoldsTheCursorWhateverThePlatformSays()
    {
        // Xorg's pointer mask has no bits past Button5, so a side button stays held until its up
        var bundle = await Started();
        bundle.Platform.SeesSwallowedButtons = true;
        bundle.Platform.UnseenButtons = [MouseButton.Extra1];
        Press(bundle.Platform, MouseButton.Extra1);

        Push(bundle.Platform, delta: true, -1);

        var stayed = bundle.Platform.IsOnVirtualScreen;
        Assert.That(stayed, Is.True);
    }

    [Test]
    public async Task RefusedEdge_IsLoggedOncePerButtonState()
    {
        var log = new CapturingLog<InputRouter>(LogLevel.Information);
        var bundle = await Started(log: log);
        Press(bundle.Platform);

        Push(bundle.Platform, delta: true, -1);
        var once = Refusals(log);
        Press(bundle.Platform, MouseButton.Right);
        Push(bundle.Platform, delta: true, -1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(once, Is.EqualTo(1), "one line however often the edge is hit");
            Assert.That(Refusals(log), Is.EqualTo(2), "another after a button event");
        }
    }

    [Test]
    public async Task HostChange_ReleasesTheButtonsHeldOnTheOldHostBeforeLeavingIt()
    {
        // the OS already had the button up while its up still sat behind the move that crossed
        var bundle = await Started(TransitionTestHelper.ChainConfig);
        bundle.Platform.SeesSwallowedButtons = true;
        Press(bundle.Platform);
        bundle.Relay.Sent.Clear();

        Push(bundle.Platform, delta: true, 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Entered(bundle.Relay, "remote2"), Is.True, "pre-condition: crossed");
            Assert.That(Buttons(bundle.Relay, "remote2"), Is.Empty, "the new host never had the button");
        }
        AssertReleasedBeforeLeaving(bundle.Relay, "remote", MouseButton.Left);
    }

    [Test]
    public async Task ReturnByHotkey_ReleasesTheButtonsHeldOnTheHostBeforeLeavingIt()
    {
        // a remote-only master's lock hotkey returns home whatever is held
        var bundle = await Services.Start(TransitionTestHelper.CreateRemoteOnlyService());
        await TransitionTestHelper.BringHostOnline(bundle.Relay, "mac");
        Assert.That(bundle.Platform.IsOnVirtualScreen, Is.True, "pre-condition: on mac");
        Press(bundle.Platform);
        bundle.Relay.Sent.Clear();

        bundle.Platform.PressHotkey('l');

        var returned = !bundle.Platform.IsOnVirtualScreen;
        Assert.That(returned, Is.True, "pre-condition: returned home");
        AssertReleasedBeforeLeaving(bundle.Relay, "mac", MouseButton.Left);
    }

    [Test]
    public async Task NoButtonForwarded_EdgesNeverAskThePlatform()
    {
        // on Xorg the question is a server round-trip, so only a drag may pay for it
        var bundle = await Started(TransitionTestHelper.ChainConfig);
        bundle.Platform.SeesSwallowedButtons = true;
        var queries = bundle.Platform.ButtonQueries;

        Push(bundle.Platform, delta: true, 1);
        Push(bundle.Platform, delta: true, -1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Entered(bundle.Relay, "remote2"), Is.True, "pre-condition: crossed");
            Assert.That(bundle.Platform.ButtonQueries, Is.EqualTo(queries));
        }
    }

    private static int Refusals(CapturingLog<InputRouter> log) =>
        log.Entries.Count(e => e.Level == LogLevel.Information && e.Message.StartsWith("Edge refused", StringComparison.Ordinal));
}
