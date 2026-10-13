using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Relay;
using Hydra.Screen;
using Tests.Setup;

namespace Tests.Screen;

// A disconnect, a relay drop or the screensaver returns the cursor through the edge it left by, like an ordinary
// return, rather than mid-screen where a visit parks it.
[TestFixture]
public class SnapBackTests
{
    // home is 2560x1440 with the remote to its right: leaving at (2559, 720) comes back at (2557, 720)
    private static readonly ScreenPoint ExitEdge = new(2557, 720);
    private static readonly ScreenPoint Park = new(1280, 720);

    private static Task<TestServiceBundle> Visiting(FakeScreenSaverSync? screenSaverSync = null, FakeScreenDetector? screens = null) =>
        TransitionTestHelper.Visiting(TransitionTestHelper.CreateService(screenSaverSync: screenSaverSync, screens: screens));

    private static async Task ChangeScreens(FakeScreenDetector screens, ScreenRect screen)
    {
        screens.Snapshot = new LocalScreenSnapshot([screen], [new ScreenInfoEntry(screen.Name, screen.X, screen.Y, screen.Width, screen.Height, 1.0m)]);
        await screens.FireChange();
    }

    // where the snap-back warped the cursor; a snap-back that did not warp fails here
    private static async Task<ScreenPoint> SnapBack(TestServiceBundle bundle)
    {
        bundle.Platform.Calls.Clear();
        await bundle.Relay.FireDisconnected();
        await bundle.Service.FlushAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bundle.Platform.IsOnVirtualScreen, Is.False, "returned");
            Assert.That(bundle.Platform.Calls, Has.Member("warp"), "the return warped");
        }
        await bundle.Service.StopAsync(CancellationToken.None);
        return new ScreenPoint(bundle.Platform.WarpX, bundle.Platform.WarpY);
    }

    [Test]
    public async Task RelayDrop_ReturnsToTheExitEdge()
    {
        Assert.That(await SnapBack(await Visiting()), Is.EqualTo(ExitEdge));
    }

    [Test]
    public async Task PeerDeparture_ReturnsToTheExitEdge()
    {
        var bundle = await Visiting();

        await bundle.Relay.FirePeersChanged();
        await bundle.Service.FlushAsync();
        await bundle.Service.StopAsync(CancellationToken.None);

        Assert.That(new ScreenPoint(bundle.Platform.WarpX, bundle.Platform.WarpY), Is.EqualTo(ExitEdge));
    }

    [Test]
    public async Task Screensaver_ReturnsToTheExitEdge()
    {
        var screenSaverSync = new FakeScreenSaverSync();
        var bundle = await Visiting(screenSaverSync);

        screenSaverSync.FireActivated();
        await bundle.Service.FlushAsync();
        await bundle.Service.StopAsync(CancellationToken.None);

        Assert.That(new ScreenPoint(bundle.Platform.WarpX, bundle.Platform.WarpY), Is.EqualTo(ExitEdge));
    }

    [Test]
    public async Task ExitScreenResized_ReturnsToItsNewEdge()
    {
        // the old point (2557, 720) is still on the wider screen, but mid-screen
        var screens = new FakeScreenDetector();
        var bundle = await Visiting(screens: screens);
        await ChangeScreens(screens, new ScreenRect("home:0", "home", 0, 0, 3840, 1440, IsLocal: true));

        Assert.That(await SnapBack(bundle), Is.EqualTo(new ScreenPoint(3837, 720)));
    }

    [Test]
    public async Task ExitScreenGone_ReturnsToThePark()
    {
        var screens = new FakeScreenDetector();
        var bundle = await Visiting(screens: screens);
        await ChangeScreens(screens, new ScreenRect("other", "home", 0, 0, 2560, 1440, IsLocal: true));

        Assert.That(await SnapBack(bundle), Is.EqualTo(Park));
    }

    [Test]
    public async Task RemoteOnlyVisit_ReturnsToThePark()
    {
        var bundle = TransitionTestHelper.CreateRemoteOnlyService();
        await bundle.Service.StartAsync(CancellationToken.None);
        await TransitionTestHelper.BringHostOnline(bundle.Relay, "mac");

        Assert.That(await SnapBack(bundle), Is.EqualTo(Park));
    }

    [Test]
    public async Task ScreensaverRestore_WhileAButtonIsHeld_StaysLocal()
    {
        var screenSaverSync = new FakeScreenSaverSync();
        var bundle = await Visiting(screenSaverSync);
        screenSaverSync.FireActivated();
        await bundle.Service.FlushAsync();
        bundle.Platform.AnyMouseButtonHeld = true;

        screenSaverSync.FireDeactivated();
        await bundle.Service.FlushAsync();

        var virtualAfter = bundle.Platform.IsOnVirtualScreen;
        await bundle.Service.StopAsync(CancellationToken.None);
        Assert.That(virtualAfter, Is.False);
    }

    [Test]
    public async Task ScreensaverRestore_ReEntersTheScreenItLeft()
    {
        var screenSaverSync = new FakeScreenSaverSync();
        var bundle = await Visiting(screenSaverSync);
        screenSaverSync.FireActivated();
        await bundle.Service.FlushAsync();
        Assert.That(bundle.Platform.IsOnVirtualScreen, Is.False, "pre-condition: snapped back");

        screenSaverSync.FireDeactivated();
        await bundle.Service.FlushAsync();

        var virtualAfter = bundle.Platform.IsOnVirtualScreen;
        await bundle.Service.StopAsync(CancellationToken.None);
        Assert.That(virtualAfter, Is.True);
    }

    [Test]
    public async Task RemoteOnly_TheClickThatEndsTheScreensaver_DoesNotEnter()
    {
        // only the restore re-enters, once, when the screensaver ends
        var screenSaverSync = new FakeScreenSaverSync();
        var bundle = TransitionTestHelper.CreateRemoteOnlyService(screenSaverSync: screenSaverSync);
        await bundle.Service.StartAsync(CancellationToken.None);
        await TransitionTestHelper.BringHostOnline(bundle.Relay, "mac");
        screenSaverSync.FireActivated();
        await bundle.Service.FlushAsync();

        bundle.Platform.FireMouseButton(new MouseButtonEvent(MouseButton.Left, false));

        var virtualAfter = bundle.Platform.IsOnVirtualScreen;
        await bundle.Service.StopAsync(CancellationToken.None);
        Assert.That(virtualAfter, Is.False);
    }

    // the screensaver interrupted a visit and ended while a button was held, so its restore is pending
    private static async Task PendingRestore(TestServiceBundle bundle, FakeScreenSaverSync screenSaverSync)
    {
        screenSaverSync.FireActivated();
        await bundle.Service.FlushAsync();
        bundle.Platform.AnyMouseButtonHeld = true;
        screenSaverSync.FireDeactivated();
        await bundle.Service.FlushAsync();
        Assert.That(bundle.Platform.IsOnVirtualScreen, Is.False, "pre-condition: refused for the held button");
        bundle.Platform.AnyMouseButtonHeld = false;
        bundle.Relay.ClearSent();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task ScreensaverRestore_RefusedForAHeldButton_IsRetriedOnItsReleaseOrAMove(bool release)
    {
        var screenSaverSync = new FakeScreenSaverSync();
        var bundle = await Visiting(screenSaverSync);
        await PendingRestore(bundle, screenSaverSync);

        if (release) bundle.Platform.FireMouseButton(new MouseButtonEvent(MouseButton.Left, false));
        else bundle.Platform.FireMouseMove(1000, 720);

        var entered = bundle.Relay.Sent<EnterScreenMessage>(MessageKind.EnterScreen, "remote");
        await bundle.Service.StopAsync(CancellationToken.None);
        Assert.That(entered, Is.Not.Empty);
    }

    [Test]
    public async Task PendingScreensaverRestore_IsCancelledByTheLockHotkey()
    {
        // an explicit lock or unlock says where the user wants the cursor; a later move must not pull it away
        var screenSaverSync = new FakeScreenSaverSync();
        var bundle = await Visiting(screenSaverSync);
        await PendingRestore(bundle, screenSaverSync);
        // locked and unlocked again, so only the cancel keeps the cursor home
        bundle.Platform.FireKeyEvent(KeyEvent.Char(KeyEventType.KeyDown, 'l', KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super));
        bundle.Platform.FireKeyEvent(KeyEvent.Char(KeyEventType.KeyDown, 'l', KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super));

        bundle.Platform.FireMouseMove(1000, 720);

        var virtualAfter = bundle.Platform.IsOnVirtualScreen;
        await bundle.Service.StopAsync(CancellationToken.None);
        Assert.That(virtualAfter, Is.False);
    }

    // a remote-only master on "win", right of "mac", which is the first remote it would otherwise enter
    private static async Task<TestServiceBundle> RemoteOnlyOnWin(FakeScreenSaverSync screenSaverSync)
    {
        var bundle = TransitionTestHelper.CreateRemoteOnlyService(TransitionTestHelper.RemoteOnlyPairConfig, screenSaverSync: screenSaverSync);
        await TransitionTestHelper.StartOnline(bundle, "mac", "win");
        for (var i = 0; i < 30; i++)
            bundle.Platform.FireMouseDelta(100, 0);
        Assert.That(bundle.Relay.Sent<EnterScreenMessage>(MessageKind.EnterScreen, "win"), Is.Not.Empty, "pre-condition: on win");
        return bundle;
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task RemoteOnly_PendingScreensaverRestore_ReturnsToTheScreenItLeft(bool release)
    {
        var screenSaverSync = new FakeScreenSaverSync();
        var bundle = await RemoteOnlyOnWin(screenSaverSync);
        await PendingRestore(bundle, screenSaverSync);

        if (release) bundle.Platform.FireMouseButton(new MouseButtonEvent(MouseButton.Left, false));
        else bundle.Platform.FireMouseMove(1000, 720);

        var entered = bundle.Relay.Snapshot().Where(m => m.Kind == MessageKind.EnterScreen).SelectMany(m => m.Targets).ToList();
        await bundle.Service.StopAsync(CancellationToken.None);
        Assert.That(entered, Is.EqualTo(["win"]));
    }

    [Test]
    public async Task RemoteOnly_PendingScreensaverRestore_AMoveDuringTheNextScreensaverEntersNothing()
    {
        var screenSaverSync = new FakeScreenSaverSync();
        var bundle = await RemoteOnlyOnWin(screenSaverSync);
        await PendingRestore(bundle, screenSaverSync);
        screenSaverSync.FireActivated();
        await bundle.Service.FlushAsync();

        bundle.Platform.FireMouseMove(1000, 720);

        var virtualAfter = bundle.Platform.IsOnVirtualScreen;
        await bundle.Service.StopAsync(CancellationToken.None);
        Assert.That(virtualAfter, Is.False);
    }

    [Test]
    public async Task RemoteOnly_ScreensaverEnds_WithItsScreenGone_EntersTheFirstRemote()
    {
        // nothing local need move: the keyboard alone must reach a remote again
        var screenSaverSync = new FakeScreenSaverSync();
        var bundle = await RemoteOnlyOnWin(screenSaverSync);
        screenSaverSync.FireActivated();
        await bundle.Service.FlushAsync();
        await bundle.Relay.FirePeersChanged("mac");
        await bundle.Service.FlushAsync();
        bundle.Relay.ClearSent();

        screenSaverSync.FireDeactivated();
        await bundle.Service.FlushAsync();

        var entered = bundle.Relay.Snapshot().Where(m => m.Kind == MessageKind.EnterScreen).SelectMany(m => m.Targets).ToList();
        await bundle.Service.StopAsync(CancellationToken.None);
        Assert.That(entered, Is.EqualTo(["mac"]));
    }
}
