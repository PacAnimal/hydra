using Hydra.Config;
using Tests.Setup;

namespace Tests.Screen;

// Every return that warps must show (or, with hideCursor, hide) the cursor BEFORE it clears the flag and warps:
// the cursor hider's pin to the park point waits for Show/Hide, not for the warp, so a later Show would let a
// pin land after the warp and leave the cursor mid-screen.
// The flag and warp are one ReturnTo, which on Windows marks the park point as the flag clears.
[TestFixture]
public class ReturnOrderTests
{
    private static readonly string[] Showing = ["show", "return", "flag:False", "warp"];
    private static readonly string[] Hiding = ["hide", "return", "flag:False", "warp"];

    private static async Task<TestServiceBundle> OnTheRemoteScreen(IHydraProfile? profile = null, FakeScreenSaverSync? screenSaverSync = null)
    {
        var bundle = await TransitionTestHelper.Visiting(TransitionTestHelper.CreateService(profile: profile, screenSaverSync: screenSaverSync));
        bundle.Platform.Calls.Clear();
        return bundle;
    }

    private static async Task AssertReturned(TestServiceBundle bundle, string[] expected)
    {
        var calls = bundle.Platform.Calls.Where(c => c is "show" or "hide" or "return" or "flag:False" or "warp").ToList();
        await bundle.Service.StopAsync(CancellationToken.None);
        Assert.That(calls, Is.EqualTo(expected));
    }

    [Test]
    public async Task EdgeReturn_ShowsBeforeItWarps()
    {
        var bundle = await OnTheRemoteScreen();

        bundle.Platform.FireMouseMove(1276, 720);

        await AssertReturned(bundle, Showing);
    }

    [Test]
    public async Task EdgeReturn_WithHideCursor_HidesBeforeItWarps()
    {
        var bundle = await OnTheRemoteScreen(TransitionTestHelper.ProfileWith(hideCursor: true));

        bundle.Platform.FireMouseMove(1276, 720);

        await AssertReturned(bundle, Hiding);
    }

    [Test]
    public async Task PeerDisconnect_ShowsBeforeItWarps()
    {
        var bundle = await OnTheRemoteScreen();

        await bundle.Relay.FirePeersChanged();
        await bundle.Service.FlushAsync();

        await AssertReturned(bundle, Showing);
    }

    [Test]
    public async Task RelayDisconnect_ShowsBeforeItWarps()
    {
        var bundle = await OnTheRemoteScreen();

        await bundle.Relay.FireDisconnected();
        await bundle.Service.FlushAsync();

        await AssertReturned(bundle, Showing);
    }

    [Test]
    public async Task Screensaver_ShowsBeforeItWarps()
    {
        var screenSaverSync = new FakeScreenSaverSync();
        var bundle = await OnTheRemoteScreen(screenSaverSync: screenSaverSync);

        screenSaverSync.FireActivated();
        await bundle.Service.FlushAsync();

        await AssertReturned(bundle, Showing);
    }
}
