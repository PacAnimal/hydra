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

    private static async Task<TestServiceBundle> Visiting(FakeScreenSaverSync? screenSaverSync = null, FakeScreenDetector? screens = null)
    {
        var bundle = TransitionTestHelper.CreateService(screenSaverSync: screenSaverSync, screens: screens);
        await bundle.Service.StartAsync(CancellationToken.None);
        await TransitionTestHelper.BringRemoteOnline(bundle.Relay);
        bundle.Platform.FireMouseMove(2559, 720);
        Assert.That(bundle.Platform.IsOnVirtualScreen, Is.True, "pre-condition: on the remote screen");
        return bundle;
    }

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
}
