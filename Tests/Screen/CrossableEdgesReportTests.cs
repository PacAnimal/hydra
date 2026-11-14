using Hydra.Platform;
using Hydra.Relay;
using Hydra.Screen;
using Microsoft.Extensions.Time.Testing;
using Tests.Setup;

namespace Tests.Screen;

[TestFixture]
public class CrossableEdgesReportTests : ServicesFixture
{
    private static readonly CrossableEdge RightEdge = new(
        new ScreenRect("home:0", "home", 0, 0, 2560, 1440, IsLocal: true), new EdgeSpan(Direction.Right, 0, 1440));

    [Test]
    public async Task Start_ReportsNoSpans_WhileTheRemoteIsOffline()
    {
        var (platform, _, _) = await Services.Start(TransitionTestHelper.CreateService());

        Assert.That(platform.CrossableEdgeReports, Has.Count.EqualTo(1));
        Assert.That(platform.CrossableEdgeReports[^1], Is.Empty);
    }

    [Test]
    public async Task RemoteScreensArriving_ReportTheEdgeTowardsIt()
    {
        var (platform, _, _) = await Services.StartOnline(TransitionTestHelper.CreateService());

        Assert.That(platform.CrossableEdgeReports[^1], Is.EqualTo([RightEdge]));
    }

    [Test]
    public async Task RemoteGoingOffline_ReportsTheEdgeGone()
    {
        var (platform, relay, _) = await Services.StartOnline(TransitionTestHelper.CreateService());
        var before = platform.CrossableEdgeReports.Count;

        await relay.FirePeersChanged();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(platform.CrossableEdgeReports, Has.Count.EqualTo(before + 1));
            Assert.That(platform.CrossableEdgeReports[^1], Is.Empty);
        }
    }

    [Test]
    public async Task LocalScreensChanging_ReportsAgain()
    {
        var screens = new FakeScreenDetector();
        var (platform, _, _) = await Services.StartOnline(TransitionTestHelper.CreateService(screens: screens));
        var before = platform.CrossableEdgeReports.Count;

        await screens.FireChange();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(platform.CrossableEdgeReports, Has.Count.EqualTo(before + 1));
            Assert.That(platform.CrossableEdgeReports[^1], Is.EqualTo([RightEdge]));
        }
    }

    [Test]
    public async Task MouseMoves_ReportNothing()
    {
        var (platform, _, _) = await Services.StartOnline(TransitionTestHelper.CreateService());
        var before = platform.CrossableEdgeReports.Count;

        platform.FireMouseMove(1280, 720);
        platform.FireMouseMove(0, 720);

        Assert.That(platform.CrossableEdgeReports, Has.Count.EqualTo(before));
    }

    [Test]
    public async Task MoveOntoAnEdgeWithNoNeighbour_StaysHome()
    {
        var (platform, _, _) = await Services.StartOnline(TransitionTestHelper.CreateService());

        platform.FireBarrierHit(0, 720, 7);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(platform.IsOnVirtualScreen, Is.False);
            Assert.That(platform.StayedHomeHits, Is.EqualTo(new long[] { 7 }));
        }
    }

    [Test]
    public async Task PlainMoveAtHome_AnswersNothing()
    {
        var (platform, _, _) = await Services.StartOnline(TransitionTestHelper.CreateService());

        platform.FireMouseMove(0, 720);

        Assert.That(platform.StayedHomeHits, Is.Empty);
    }

    [Test]
    public async Task MoveThatEnters_DoesNotStayHome()
    {
        var (platform, _, _) = await Services.StartOnline(TransitionTestHelper.CreateService());

        platform.FireBarrierHit(2559, 720, 3);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(platform.IsOnVirtualScreen, Is.True);
            Assert.That(platform.StayedHomeHits, Is.Empty);
        }
    }

    // a hit queued behind the entry it raced is a local edge point, no motion on the remote
    [Test]
    public async Task HitDuringAVisit_MovesNothingAndAnswersNothing()
    {
        var timers = new FakeTimeProvider();
        var (platform, relay, _) = await Services.Visiting(TransitionTestHelper.CreateService(timeProvider: timers));
        relay.Sent.Clear();
        timers.Advance(TimeSpan.FromSeconds(1));

        platform.FireBarrierHit(platform.WarpX + 5, platform.WarpY, 9);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(platform.IsOnVirtualScreen, Is.True);
            Assert.That(relay.Sent.Of(MessageKind.MouseMove), Is.Empty);
            Assert.That(platform.StayedHomeHits, Is.Empty);
        }
    }
}
