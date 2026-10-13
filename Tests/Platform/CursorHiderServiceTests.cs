using Hydra.Platform;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.Platform;

[TestFixture]
public class CursorHiderServiceTests
{
    private const int ParkX = 960, ParkY = 540;

    // hidden, with the pending hide already carried out, so the next run is the pin (if there is one)
    private static async Task<CursorHiderService> Hidden(FakePlatform platform)
    {
        var hider = new CursorHiderService(NullLogger<CursorHiderService>.Instance, platform);
        hider.UpdateWarpPoint(ParkX, ParkY);
        hider.Hide();
        await hider.ExecuteOnce();
        return hider;
    }

    // hidden for a remote visit on a platform the router recentres (macOS, Xorg), where the hider pins
    private static Task<CursorHiderService> HiddenForAVisit(FakePlatform platform)
    {
        platform.IsOnVirtualScreen = true;
        return Hidden(platform);
    }

    [Test]
    public async Task ShowDuringAPin_WaitsForThePinToLand()
    {
        // the router shows the cursor before its return warp, so a pin that finished after Show could land after that warp
        var platform = new FakePlatform();
        using var hider = await HiddenForAVisit(platform);
        Action showCursor = hider.Show;
        Thread? show = null;
        var showFinishedDuringThePin = false;
        platform.OnWarp = () =>
        {
            show = new Thread(() => showCursor());
            show.Start();
            SpinWait.SpinUntil(() => !show.IsAlive || show.ThreadState.HasFlag(ThreadState.WaitSleepJoin));
            showFinishedDuringThePin = !show.IsAlive;
        };

        await hider.ExecuteOnce();
        show!.Join();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(platform.WarpCount, Is.EqualTo(1), "the run must have pinned for the test to mean anything");
            Assert.That(showFinishedDuringThePin, Is.False);
        }
    }

    [Test]
    public async Task AfterShow_NothingIsPinned()
    {
        var platform = new FakePlatform();
        using var hider = await HiddenForAVisit(platform);
        hider.Show();
        await hider.ExecuteOnce(); // the pending show

        await hider.ExecuteOnce();

        Assert.That(platform.WarpCount, Is.Zero);
    }

    [Test]
    public async Task OnAVisit_PinsToThePark()
    {
        var platform = new FakePlatform();
        using var hider = await HiddenForAVisit(platform);

        await hider.ExecuteOnce();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(platform.WarpCount, Is.EqualTo(1));
            Assert.That((platform.WarpX, platform.WarpY), Is.EqualTo((ParkX, ParkY)));
        }
    }

    [Test]
    public async Task OnTheLocalScreen_HidesInPlace()
    {
        var platform = new FakePlatform();
        using var hider = await Hidden(platform);

        await hider.ExecuteOnce();

        Assert.That(platform.WarpCount, Is.Zero, "a hidden local cursor must reappear where it was, not at the centre");
    }

    [Test]
    public async Task OnAVisit_PlatformThatRecentresItself_IsNotPinned()
    {
        var platform = new FakePlatform { RecentresItself = true };
        using var hider = await HiddenForAVisit(platform);

        await hider.ExecuteOnce();

        Assert.That(platform.WarpCount, Is.Zero, "the platform parks the cursor itself");
    }

    [Test]
    public async Task WithoutAPin_ACursorThatBecameVisibleIsHiddenAgain()
    {
        var platform = new FakePlatform();
        using var hider = await Hidden(platform);
        platform.CursorIsVisible = true;
        platform.HideCursorCalled = false;

        await hider.ExecuteOnce();

        Assert.That(platform.HideCursorCalled, Is.True);
    }
}
