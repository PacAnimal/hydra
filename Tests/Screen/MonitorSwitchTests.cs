using Hydra.Config;
using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Relay;
using Hydra.Screen;
using Tests.Setup;

namespace Tests.Screen;

// the monitor switch hotkey through the router: on releasing the arrow, the cursor goes to the centre of the screen
// that way, local or remote, cursor lock or not
[TestFixture]
public class MonitorSwitchTests : ServicesFixture
{
    [Test]
    public async Task FromHome_EntersTheRemoteAtItsCentre_OnTheRelease()
    {
        var (platform, relay, _) = await Services.StartOnline(TransitionTestHelper.CreateService());

        platform.PressHotkey(SpecialKey.Right);
        var beforeRelease = relay.Sent.Of(MessageKind.EnterScreen).Count;
        platform.PressHotkey(SpecialKey.Right, up: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(beforeRelease, Is.Zero, "nothing moves until the arrow is let go");
            Assert.That(platform.IsOnVirtualScreen, Is.True);
            // the fake reads the input as X11 does, where the switch's own keys hide the cursor (xbanish)
            Assert.That(Entered(relay, "remote"), Is.EqualTo(new EnterScreenMessage("remote", 1280, 720, 2560, 1440, Focus: true, CursorHidden: true)));
        }
    }

    [Test]
    public async Task FromTheRemote_ReturnsHomeToItsCentre()
    {
        var (platform, relay, _) = await Services.Visiting(TransitionTestHelper.CreateService());
        platform.Calls.Clear();

        Switch(platform, SpecialKey.Left);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(platform.IsOnVirtualScreen, Is.False);
            Assert.That(platform.Calls, Does.Contain("return"));
            Assert.That((platform.WarpX, platform.WarpY), Is.EqualTo((1280, 720)));
            Assert.That(relay.Sent.Of(MessageKind.LeaveScreen, "remote"), Has.Count.EqualTo(1));
        }
    }

    // from one of a remote's monitors to its other: the visit goes on, with no leave in between
    [Test]
    public async Task BetweenARemotesMonitors_MovesWithinTheVisit()
    {
        var (platform, relay, _) = await Services.StartOnline(TransitionTestHelper.CreateService());
        await TransitionTestHelper.AdvertiseScreens(relay, "remote", 2);
        TransitionTestHelper.EnterRemote(platform);
        relay.Sent.Clear();

        Switch(platform, SpecialKey.Right);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Entered(relay, "remote"), Is.EqualTo(new EnterScreenMessage("remote:1", 1280, 720, 2560, 1440, Focus: true)));
            Assert.That(relay.Sent.Of(MessageKind.LeaveScreen), Is.Empty);
        }
    }

    [Test]
    public async Task FromOneRemoteToAnother_LeavesTheFirst()
    {
        var (platform, relay, _) = await Services.StartOnline(TransitionTestHelper.CreateService(profile: TransitionTestHelper.ChainConfig), "remote", "remote2");
        TransitionTestHelper.EnterRemote(platform);
        relay.Sent.Clear();

        Switch(platform, SpecialKey.Right);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(relay.Sent.Of(MessageKind.LeaveScreen, "remote"), Has.Count.EqualTo(1));
            Assert.That(Entered(relay, "remote2"), Is.EqualTo(new EnterScreenMessage("remote2", 1280, 720, 2560, 1440, Focus: true)));
        }
    }

    // between two local monitors, the cursor is warped there and nothing is sent
    [Test]
    public async Task BetweenLocalMonitors_WarpsToTheOthersCentre()
    {
        var screens = new FakeScreenDetector
        {
            Snapshot = new LocalScreenSnapshot(
                [new ScreenRect("home:0", "home", 0, 0, 2560, 1440, IsLocal: true), new ScreenRect("home:1", "home", 0, -1080, 1920, 1080, IsLocal: true)],
                [new ScreenInfoEntry("home:0", 0, 0, 2560, 1440, 1.0m), new ScreenInfoEntry("home:1", 0, -1080, 1920, 1080, 1.0m)]),
        };
        var (platform, relay, _) = await Services.StartOnline(TransitionTestHelper.CreateService(screens: screens));
        relay.Sent.Clear();

        Switch(platform, SpecialKey.Up);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(platform.IsOnVirtualScreen, Is.False);
            Assert.That((platform.WarpX, platform.WarpY), Is.EqualTo((960, -540)));
            Assert.That(relay.Sent.Snapshot(), Is.Empty);
        }
    }

    // Xorg reports the chord's own keys as held when the arrow is released; an edge crossing would refuse that entry
    [Test]
    public async Task TheChordStillHeld_DoesNotRefuseTheEntry()
    {
        var (platform, relay, _) = await Services.StartOnline(TransitionTestHelper.CreateService());
        platform.AnyKeyHeld = true;

        platform.FireMouseMove(2559, 720);
        var crossedTheEdge = platform.IsOnVirtualScreen;
        Switch(platform, SpecialKey.Right);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(crossedTheEdge, Is.False, "pre-condition: an edge refuses an entry with a key held");
            Assert.That(platform.IsOnVirtualScreen, Is.True);
            Assert.That(Entered(relay, "remote"), Is.Not.Null);
        }
    }

    [Test]
    public async Task WithTheCursorLocked_ItStillMoves()
    {
        var (platform, _, _) = await Services.Visiting(TransitionTestHelper.CreateService());
        platform.PressHotkey('l');
        platform.PressHotkey('l', up: true);

        Switch(platform, SpecialKey.Left);

        Assert.That(platform.IsOnVirtualScreen, Is.False);
    }

    // a button pressed on the remote keeps the cursor there, as at an edge, so its release reaches it
    [Test]
    public async Task AButtonHeldOnTheRemote_KeepsTheCursorThere()
    {
        var (platform, relay, _) = await Services.Visiting(TransitionTestHelper.CreateService());
        platform.FireMouseButton(new MouseButtonEvent(MouseButton.Left, true));

        Switch(platform, SpecialKey.Left);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(platform.IsOnVirtualScreen, Is.True);
            Assert.That(relay.Sent.Of(MessageKind.LeaveScreen), Is.Empty);
        }
    }

    // a hidden cursor stays hidden where it lands
    [Test]
    public async Task WithHideCursor_TheReturnLeavesItHidden()
    {
        var (platform, _, _) = await Services.Visiting(TransitionTestHelper.CreateService(profile: TransitionTestHelper.ProfileWith(hideCursor: true)));
        platform.ShowCursorCalled = false;

        Switch(platform, SpecialKey.Left);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(platform.IsOnVirtualScreen, Is.False);
            Assert.That(platform.ShowCursorCalled, Is.False);
        }
    }

    [Test]
    public async Task NothingThatWay_MovesNothing()
    {
        var (platform, relay, _) = await Services.StartOnline(TransitionTestHelper.CreateService());
        relay.Sent.Clear();
        var warps = platform.WarpCount;

        Switch(platform, SpecialKey.Up);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(platform.IsOnVirtualScreen, Is.False);
            Assert.That(platform.WarpCount, Is.EqualTo(warps));
            Assert.That(relay.Sent.Snapshot(), Is.Empty);
        }
    }

    // the arrows never reach the remote: their downs are the hotkey's, so their ups are not forwarded either
    [Test]
    public async Task TheArrowsAreNeverForwarded()
    {
        var (platform, relay, _) = await Services.StartOnline(TransitionTestHelper.CreateService(profile: TransitionTestHelper.ChainConfig), "remote", "remote2");
        TransitionTestHelper.EnterRemote(platform);
        relay.Sent.Clear();

        Switch(platform, SpecialKey.Right);

        Assert.That(relay.Sent.Of(MessageKind.KeyEvent).Concat(relay.Sent.Of(MessageKind.KeyEventBatch)), Is.Empty);
    }

    // arriving on one of the master's own screens, the window last worked in there comes forward
    [Test]
    public async Task ArrivingHome_FocusesAWindowThere()
    {
        var focus = new RecordingWindowFocus();
        var (platform, _, _) = await Services.Visiting(TransitionTestHelper.CreateService(windowFocus: focus));

        Switch(platform, SpecialKey.Left);

        Assert.That(focus.Focused, Is.EqualTo([new ScreenBounds(0, 0, 2560, 1440)]));
    }

    // arriving on a remote, the slave is asked to; an edge crossing never asks
    [Test]
    public async Task ArrivingOnARemote_AsksItToFocus_AndAnEdgeDoesNot()
    {
        var focus = new RecordingWindowFocus();
        var (platform, relay, _) = await Services.StartOnline(TransitionTestHelper.CreateService(windowFocus: focus));
        await TransitionTestHelper.AdvertiseScreens(relay, "remote", 2);

        TransitionTestHelper.EnterRemote(platform);
        var byEdge = Entered(relay, "remote");
        Switch(platform, SpecialKey.Right);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(byEdge?.Focus, Is.False);
            Assert.That(Entered(relay, "remote"), Is.EqualTo(new EnterScreenMessage("remote:1", 1280, 720, 2560, 1440, Focus: true)));
            Assert.That(focus.Focused, Is.Empty, "the master focuses only its own screens");
        }
    }

    // an Xorg visit is announced only once the keyboard is won; that late announcement still asks for the focus
    [Test]
    public async Task AnArrivalAnnouncedLate_StillAsksToFocus()
    {
        var (platform, relay, _) = await Services.StartOnline(TransitionTestHelper.CreateService());
        platform.KeyboardCaptured = false;

        Switch(platform, SpecialKey.Right);
        var beforeTheGrab = relay.Sent.Of(MessageKind.EnterScreen).Count;
        platform.KeyboardCaptured = true;
        platform.FireKeyboardCapturedLate();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(beforeTheGrab, Is.Zero);
            Assert.That(Entered(relay, "remote")?.Focus, Is.True);
        }
    }

    // a remote-only master the user unlocked to its own screen: switching onto a remote puts input there as its entry
    // does, so one press of the lock hotkey hands it back
    [Test]
    public async Task RemoteOnly_SwitchingBackOntoARemote_IsInputOnTheRemote()
    {
        var profile = TransitionTestHelper.Profile("pi", new HydraConfig
        {
            Mode = Mode.Master,
            RemoteOnly = true,
            Hosts =
            [
                new HostConfig { Name = "pi", Neighbours = [new NeighbourConfig { Direction = Direction.Right, Name = "mac" }] },
                new HostConfig { Name = "mac", Neighbours = [new NeighbourConfig { Direction = Direction.Left, Name = "pi" }] },
            ],
        });
        var screens = new FakeScreenDetector
        {
            Snapshot = new LocalScreenSnapshot([new ScreenRect("pi", "pi", 0, 0, 1920, 1080, IsLocal: true)], [new ScreenInfoEntry("pi", 0, 0, 1920, 1080, 1.0m)]),
        };
        var (platform, _, _) = await Services.StartOnline(TransitionTestHelper.CreateService(profile: profile, screens: screens), "mac");
        Assert.That(platform.IsOnVirtualScreen, Is.True, "pre-condition: a remote-only master enters its remote");
        platform.PressHotkey('l');
        Assert.That(platform.IsOnVirtualScreen, Is.False, "pre-condition: the lock hotkey hands input to this machine");

        Switch(platform, SpecialKey.Right);
        var onTheRemote = platform.IsOnVirtualScreen;
        platform.PressHotkey('l');

        using (Assert.EnterMultipleScope())
        {
            Assert.That(onTheRemote, Is.True);
            Assert.That(platform.IsOnVirtualScreen, Is.False, "one press takes input back to this machine");
        }
    }

    // Super held on the remote through a switch there: its release is no bare tap, or a Windows slave opens Start and a
    // GNOME one its overview over the window just focused
    [Test]
    public async Task AModifierHeldThroughAHotkey_IsReleasedAsChorded_AndAPlainTapIsNot()
    {
        var (platform, relay, _) = await Services.StartOnline(TransitionTestHelper.CreateService());
        await TransitionTestHelper.AdvertiseScreens(relay, "remote", 2);
        TransitionTestHelper.EnterRemote(platform);

        Modifier(platform, KeyEventType.KeyDown);
        Switch(platform, SpecialKey.Right);
        Modifier(platform, KeyEventType.KeyUp);
        Modifier(platform, KeyEventType.KeyDown);
        Modifier(platform, KeyEventType.KeyUp);

        var ups = relay.Sent.Of<KeyEventMessage>(MessageKind.KeyEvent, "remote").Where(k => k.Type == KeyEventType.KeyUp).ToList();
        Assert.That(ups.Select(k => k.Chorded), Is.EqualTo(new bool?[] { true, null }));
    }

    private static void Modifier(FakePlatform platform, KeyEventType type) =>
        platform.FireKeyEvent(KeyEvent.Special(type, SpecialKey.Super_L, type == KeyEventType.KeyDown ? KeyModifiers.Super : KeyModifiers.None) with { PhysicalKey = 55 });

    // -- the cursor's visibility goes with the switch (CursorHiddenAt) --

    [Test]
    public async Task FromHome_WithTheCursorShown_ArrivesShown()
    {
        var (platform, relay, _) = await Services.StartOnline(TransitionTestHelper.CreateService());
        platform.ShownAtHome = true;

        Switch(platform, SpecialKey.Right);

        Assert.That(Entered(relay, "remote")?.CursorHidden, Is.False);
    }

    [Test]
    public async Task FromHome_WithTheCursorHidden_ArrivesHidden()
    {
        var (platform, relay, _) = await Services.StartOnline(TransitionTestHelper.CreateService());
        platform.ShownAtHome = false;

        Switch(platform, SpecialKey.Right);

        Assert.That(Entered(relay, "remote")?.CursorHidden, Is.True);
    }

    // on a remote, its hide-on-typing tool sees only what is forwarded, and the switch's own arrow never is
    [Test]
    public async Task FromARemote_TheMouseUsedLast_ArrivesShown()
    {
        var (platform, relay, _) = await Services.StartOnline(TransitionTestHelper.CreateService(profile: TransitionTestHelper.ChainConfig), "remote", "remote2");
        TransitionTestHelper.EnterRemote(platform);
        Type(platform, 'a');
        platform.FireMouseDelta(5, 0);

        Switch(platform, SpecialKey.Right);

        Assert.That(Entered(relay, "remote2")?.CursorHidden, Is.False);
    }

    [Test]
    public async Task FromARemote_TypedSinceTheMouse_ArrivesHidden()
    {
        var (platform, relay, _) = await Services.StartOnline(TransitionTestHelper.CreateService(profile: TransitionTestHelper.ChainConfig), "remote", "remote2");
        TransitionTestHelper.EnterRemote(platform);
        Type(platform, 'a');

        Switch(platform, SpecialKey.Right);

        Assert.That(Entered(relay, "remote2")?.CursorHidden, Is.True);
    }

    [Test]
    public async Task HomeFromARemote_TypedSinceTheMouse_StaysHiddenUntilTheMouseMoves()
    {
        var (platform, _, _) = await Services.Visiting(TransitionTestHelper.CreateService());
        Type(platform, 'a');
        platform.Calls.Clear();

        Switch(platform, SpecialKey.Left);
        var atReturn = platform.Calls.ToList();
        platform.Calls.Clear();
        platform.FireMouseMove(1290, 720);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(atReturn.SkipWhile(c => c != "return").Skip(1), Does.Contain("hide"), "hidden after the return");
            Assert.That(platform.Calls, Does.Contain("show"), "shown once the mouse moves");
        }
    }

    [Test]
    public async Task HomeFromARemote_TheMouseUsedLast_ArrivesShown()
    {
        var (platform, _, _) = await Services.Visiting(TransitionTestHelper.CreateService());
        Type(platform, 'a');
        platform.FireMouseDelta(5, 0);
        platform.Calls.Clear();

        Switch(platform, SpecialKey.Left);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(platform.Calls, Does.Contain("return"));
            Assert.That(platform.Calls.SkipWhile(c => c != "return"), Does.Not.Contain("hide"));
        }
    }

    private static void Type(FakePlatform platform, char ch)
    {
        platform.FireKeyEvent(KeyEvent.Char(KeyEventType.KeyDown, ch, KeyModifiers.None));
        platform.FireKeyEvent(KeyEvent.Char(KeyEventType.KeyUp, ch, KeyModifiers.None));
    }

    private static void Switch(FakePlatform platform, SpecialKey arrow)
    {
        platform.PressHotkey(arrow);
        platform.PressHotkey(arrow, up: true);
    }

    private static EnterScreenMessage? Entered(FakeRelay relay, string host) =>
        relay.Sent.Of<EnterScreenMessage>(MessageKind.EnterScreen, host).LastOrDefault();
}
