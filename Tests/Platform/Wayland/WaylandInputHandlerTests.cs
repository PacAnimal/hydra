using Hydra.Mouse;
using Hydra.Platform;
using Hydra.Platform.Linux;
using Hydra.Platform.Linux.Wayland;
using Hydra.Screen;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.Platform.Wayland;

[TestFixture]
public class WaylandInputHandlerTests
{
    private static ScreenRect Main => new("home:0", "home", 0, 0, 1920, 1080, IsLocal: true);
    private static ScreenRect Second => new("home:1", "home", 1920, 0, 2560, 1440, IsLocal: true);
    private static ScreenRect Below => new("home:1", "home", 0, 1080, 1920, 1080, IsLocal: true);

    private static CrossableEdge Edge(ScreenRect screen, Direction dir, int start, int end) => new(screen, new EdgeSpan(dir, start, end));

    private static IReadOnlyList<BarrierLine> Lines(params CrossableEdge[] edges) => WaylandBarriers.Lines(edges, []);

    private static List<string> Positions(IEnumerable<BarrierLine> lines) => [.. lines.Select(l => At(l.X1, l.Y1, l.X2, l.Y2))];

    [Test]
    public void LeftEdge_LiesOnTheScreensFirstColumn()
    {
        Assert.That(Positions(Lines(Edge(Main, Direction.Left, 0, 1080))), Is.EqualTo([At(0, 0, 0, 1079)]));
    }

    [Test]
    public void RightEdge_LiesPastTheScreensLastColumn()
    {
        Assert.That(Positions(Lines(Edge(Main, Direction.Right, 0, 1080))), Is.EqualTo([At(1920, 0, 1920, 1079)]));
    }

    [Test]
    public void TopAndBottom_LieOnTheFirstRowAndPastTheLast()
    {
        var lines = Lines(Edge(Main, Direction.Up, 0, 1920), Edge(Main, Direction.Down, 0, 1920));

        Assert.That(Positions(lines), Is.EqualTo([At(0, 0, 1919, 0), At(0, 1080, 1919, 1080)]));
    }

    [Test]
    public void APartialSpan_GetsTheWholeEdge()
    {
        // a link along part of the edge, or a dead corner, still bars the whole edge; the router decides where it crosses
        var lines = Lines(Edge(Second, Direction.Right, 100, 400), Edge(Second, Direction.Down, 2000, 2560));

        Assert.That(Positions(lines), Is.EqualTo([At(4480, 0, 4480, 1439), At(1920, 1440, 4479, 1440)]));
    }

    [Test]
    public void SeveralSpansOnOneEdge_AreOneBarrier()
    {
        var lines = Lines(Edge(Main, Direction.Left, 0, 300), Edge(Main, Direction.Left, 600, 1080));

        Assert.That(Positions(lines), Is.EqualTo([At(0, 0, 0, 1079)]));
    }

    [Test]
    public void SideBySide_BothSidesOfTheSeamAreDropped()
    {
        // Second is taller than Main, yet its left edge touches Main along part of it, so it gets none at all
        var lines = Lines(Edge(Main, Direction.Right, 0, 1080), Edge(Second, Direction.Left, 0, 1440), Edge(Main, Direction.Left, 0, 1080));

        Assert.That(Positions(lines), Is.EqualTo([At(0, 0, 0, 1079)]));
    }

    [Test]
    public void SideBySide_ATallerScreensPartlyFreeEdge_IsDropped()
    {
        // KDE refuses a barrier short of its edge, and part of this one touches Main: nothing is left to bar
        var lines = WaylandBarriers.Lines([Edge(Second, Direction.Left, 1080, 1440)], [Main.Bounds]);

        Assert.That(lines, Is.Empty);
    }

    [Test]
    public void SideBySide_ATopEdgeEndingOnTheSeamIsKept()
    {
        // the barrier's end column is the screen's own last pixel, short of the next screen
        var lines = WaylandBarriers.Lines([Edge(Main, Direction.Up, 0, 1920), Edge(Second, Direction.Up, 0, 2560)], []);

        Assert.That(Positions(lines), Is.EqualTo([At(0, 0, 1919, 0), At(1920, 0, 4479, 0)]));
    }

    [Test]
    public void Stacked_TheSharedSeamIsDropped()
    {
        var lines = Lines(Edge(Main, Direction.Down, 0, 1920), Edge(Below, Direction.Up, 0, 1920), Edge(Below, Direction.Down, 0, 1920));

        Assert.That(Positions(lines), Is.EqualTo([At(0, 2160, 1919, 2160)]));
    }

    [Test]
    public void Stacked_ScreensKnownOnlyToTheSessionStillCount()
    {
        var lines = WaylandBarriers.Lines([Edge(Main, Direction.Down, 0, 1920)], [Below.Bounds]);

        Assert.That(lines, Is.Empty);
    }

    [Test]
    public void Stacked_ANarrowerScreenBelow_DropsTheWholeEdge()
    {
        var narrow = new ScreenRect("home:1", "home", 500, 1080, 1000, 800, IsLocal: true);

        var lines = WaylandBarriers.Lines([Edge(Main, Direction.Down, 0, 1920)], [narrow.Bounds]);

        Assert.That(lines, Is.Empty);
    }

    [Test]
    public void AScreenMeetingOnlyAtACorner_LeavesTheEdge()
    {
        var diagonal = new ScreenRect("home:1", "home", 1920, 1080, 1920, 1080, IsLocal: true);

        var lines = WaylandBarriers.Lines([Edge(Main, Direction.Right, 0, 1080), Edge(Main, Direction.Down, 0, 1920)], [diagonal.Bounds]);

        Assert.That(Positions(lines), Is.EqualTo([At(1920, 0, 1920, 1079), At(0, 1080, 1919, 1080)]));
    }

    [Test]
    public void ASinglePixelSpan_StillBarsTheEdge()
    {
        Assert.That(Positions(Lines(Edge(Main, Direction.Left, 10, 11))), Is.EqualTo([At(0, 0, 0, 1079)]));
    }

    [Test]
    public void Hit_OnTheRightEdge_LandsOnTheLastColumn()
    {
        var barrier = Lines(Edge(Second, Direction.Right, 100, 400))[0];

        Assert.That(WaylandBarriers.HitPoint(barrier, 4530.7, 250.4), Is.EqualTo(new ScreenPoint(4479, 250)));
    }

    [Test]
    public void Hit_PastTheEdgesEnds_IsClampedOntoIt()
    {
        var barrier = Lines(Edge(Second, Direction.Right, 100, 400))[0];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(WaylandBarriers.HitPoint(barrier, 4490, 1500), Is.EqualTo(new ScreenPoint(4479, 1439)));
            Assert.That(WaylandBarriers.HitPoint(barrier, 4490, -3), Is.EqualTo(new ScreenPoint(4479, 0)));
        }
    }

    [Test]
    public void Hit_OnTopOrBottom_StaysOffTheCornerColumns()
    {
        // the router reads a corner pixel as the side edge's
        var up = Lines(Edge(Main, Direction.Up, 0, 1920))[0];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(WaylandBarriers.HitPoint(up, -40, -3), Is.EqualTo(new ScreenPoint(1, 0)));
            Assert.That(WaylandBarriers.HitPoint(up, 1919.5, -3), Is.EqualTo(new ScreenPoint(1918, 0)));
        }
    }

    [Test]
    public void Hit_OnEveryEdge_LandsOnItsOutermostPixel()
    {
        var left = Lines(Edge(Main, Direction.Left, 0, 1080))[0];
        var up = Lines(Edge(Main, Direction.Up, 0, 1920))[0];
        var down = Lines(Edge(Main, Direction.Down, 0, 1920))[0];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(WaylandBarriers.HitPoint(left, -25, 500.5), Is.EqualTo(new ScreenPoint(0, 500)));
            Assert.That(WaylandBarriers.HitPoint(up, 700, -3), Is.EqualTo(new ScreenPoint(700, 0)));
            Assert.That(WaylandBarriers.HitPoint(down, 700, 1100), Is.EqualTo(new ScreenPoint(700, 1079)));
        }
    }

    [Test]
    public void Release_IsOnePixelInsideTheHit()
    {
        var right = Lines(Edge(Second, Direction.Right, 0, 1440))[0];
        var left = Lines(Edge(Main, Direction.Left, 0, 1080))[0];
        var up = Lines(Edge(Main, Direction.Up, 0, 1920))[0];
        var down = Lines(Edge(Main, Direction.Down, 0, 1920))[0];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(WaylandBarriers.ReleasePoint(right, new ScreenPoint(4479, 250)), Is.EqualTo(new ScreenPoint(4478, 250)));
            Assert.That(WaylandBarriers.ReleasePoint(left, new ScreenPoint(0, 500)), Is.EqualTo(new ScreenPoint(1, 500)));
            Assert.That(WaylandBarriers.ReleasePoint(up, new ScreenPoint(700, 0)), Is.EqualTo(new ScreenPoint(700, 1)));
            Assert.That(WaylandBarriers.ReleasePoint(down, new ScreenPoint(700, 1079)), Is.EqualTo(new ScreenPoint(700, 1078)));
        }
    }

    [Test]
    public void Nearest_FindsTheBarrierAHitWithNoIdCrossed()
    {
        var lines = Lines(Edge(Main, Direction.Left, 0, 1080), Edge(Main, Direction.Right, 0, 1080));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(WaylandBarriers.Nearest(lines, 1950, 300), Is.SameAs(lines[1]));
            Assert.That(WaylandBarriers.Nearest([], 1950, 300), Is.Null);
        }
    }

    [Test]
    public void DiscreteScroll_FlipsVerticalOnly()
    {
        using (Assert.EnterMultipleScope())
        {
            // EIS: positive dy is down, positive dx right; the wire's Y is up
            Assert.That(WaylandInputHandler.DiscreteScroll(0, 120), Is.EqualTo(new MouseScrollEvent(0, -120)));
            Assert.That(WaylandInputHandler.DiscreteScroll(-120, 0), Is.EqualTo(new MouseScrollEvent(-120, 0)));
        }
    }

    [Test]
    public void SmoothScroll_CarriesTheFraction()
    {
        var carry = 0.0;

        // a notch is 10 delta units, so 0.05 is 0.6 of a wire unit
        Assert.That(WaylandInputHandler.SmoothScroll(0.05, ref carry), Is.Zero);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(WaylandInputHandler.SmoothScroll(0.05, ref carry), Is.EqualTo(1));
            Assert.That(WaylandInputHandler.SmoothScroll(10, ref carry), Is.EqualTo(120));
        }
    }

    [TestCase(0x110u, MouseButton.Left)]
    [TestCase(0x111u, MouseButton.Right)]
    [TestCase(0x112u, MouseButton.Middle)]
    [TestCase(0x113u, MouseButton.Extra1)]
    [TestCase(0x114u, MouseButton.Extra2)]
    public void Buttons_MapBothWays(uint code, MouseButton button)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(EvdevButtons.Button(code), Is.EqualTo(button));
            Assert.That(EvdevButtons.Code(button), Is.EqualTo(code));
        }
    }

    [Test]
    public void Buttons_PastHydrasNames_AreExtra2()
    {
        // BTN_FORWARD
        Assert.That(EvdevButtons.Button(0x115), Is.EqualTo(MouseButton.Extra2));
    }

    [Test]
    public void Buttons_AnUpWithNoDownThisCapture_IsDropped()
    {
        var held = new HashSet<uint>();

        using (Assert.EnterMultipleScope())
        {
            // held as the capture began: its up has no down here
            Assert.That(WaylandInputHandler.TrackButton(held, 0x110, pressed: false), Is.False);
            Assert.That(WaylandInputHandler.TrackButton(held, 0x111, pressed: true), Is.True);
            Assert.That(held, Is.EquivalentTo((uint[])[0x111u]));
            Assert.That(WaylandInputHandler.TrackButton(held, 0x111, pressed: false), Is.True);
            Assert.That(held, Is.Empty);
        }
    }

    [Test]
    public void BarrierIds_NeverRepeatTheLastSet()
    {
        var last = 0u;
        var first = WaylandBarriers.Ids(ref last, 3);
        var second = WaylandBarriers.Ids(ref last, 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.EqualTo([1u, 2u, 3u]));
            Assert.That(second, Is.EqualTo([4u, 5u]));
        }
    }

    [Test]
    public void BarrierIds_WrapPastZero()
    {
        var last = uint.MaxValue - 1;

        Assert.That(WaylandBarriers.Ids(ref last, 3), Is.EqualTo([uint.MaxValue, 1u, 2u]));
    }

    [Test]
    public void Backoff_DoublesUpToItsCap()
    {
        var first = TimeSpan.FromSeconds(1);
        var max = TimeSpan.FromSeconds(60);

        Assert.That(Enumerable.Range(0, 8).Select(i => WaylandBarriers.Backoff(i, first, max).TotalSeconds).ToList(),
            Is.EqualTo(new double[] { 1, 2, 4, 8, 16, 32, 60, 60 }));
    }

    [Test]
    public void Zones_AreTheirBounds()
    {
        var zones = new PortalZoneSet(7, [new PortalZone(0, 0, 1920, 1080), new PortalZone(1920, 0, 2560, 1440)]);

        Assert.That(WaylandBarriers.Bounds(zones).ToList(), Is.EqualTo([Main.Bounds, Second.Bounds]));
    }

    // a line's endpoints, as the assertions compare them
    private static string At(int x1, int y1, int x2, int y2) => $"({x1},{y1})-({x2},{y2})";
}

[TestFixture]
public class MotionSumTests
{
    [Test]
    public void AFrameOfMotion_IsSummed()
    {
        var motion = new MotionSum();
        motion.Add(1.5, -2, visiting: true);
        motion.Add(0.5, -1, visiting: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(motion.Take(visiting: true), Is.EqualTo(new FrameMotion(2, -3)));
            Assert.That(motion.Take(visiting: true), Is.Null, "the next frame starts empty");
        }
    }

    [Test]
    public void MotionBeforeTheEntry_BelongsToNoVisit()
    {
        // a barrier hit the router has not entered yet: the user's wobble at the edge
        var motion = new MotionSum();
        motion.Add(-12, 3, visiting: false);

        Assert.That(motion.Take(visiting: false), Is.Null);
        motion.Add(4, 0, visiting: true);
        Assert.That(motion.Take(visiting: true), Is.EqualTo(new FrameMotion(4, 0)));
    }

    [Test]
    public void AFrameStartedBeforeTheEntry_IsNotCarriedIntoIt()
    {
        var motion = new MotionSum();
        motion.Add(-12, 0, visiting: false);
        motion.Add(3, 0, visiting: true);

        Assert.That(motion.Take(visiting: true), Is.EqualTo(new FrameMotion(3, 0)));
    }

    [Test]
    public void TheEntryReset_DropsAHalfSummedFrame()
    {
        var motion = new MotionSum();
        motion.Add(-5, 0, visiting: true);
        motion.Reset();

        Assert.That(motion.Take(visiting: true), Is.Null);
    }

    [Test]
    public void AFrameEndingAfterTheReturn_IsDropped()
    {
        var motion = new MotionSum();
        motion.Add(7, 1, visiting: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(motion.Take(visiting: false), Is.Null);
            Assert.That(motion.Take(visiting: true), Is.Null, "nor kept for a later visit");
        }
    }
}

[TestFixture]
public class SerialWorkTests
{
    private static readonly TimeSpan Long = TimeSpan.FromMinutes(1);

    [Test]
    public async Task ALaneRunsInOrder_AnotherDoesNotWaitOnIt()
    {
        var barriers = new SerialWork(NullLogger.Instance);
        var releases = new SerialWork(NullLogger.Instance);
        var stuck = new TaskCompletionSource();
        var ran = new List<string>();

        var parked = barriers.Enqueue("barriers", _ => stuck.Task, Long, CancellationToken.None);
        var after = barriers.Enqueue("after", _ =>
        {
            lock (ran) ran.Add("after");
            return Task.CompletedTask;
        }, Long, CancellationToken.None);
        var released = await releases.Enqueue("release", _ =>
        {
            lock (ran) ran.Add("release");
            return Task.CompletedTask;
        }, Long, CancellationToken.None).WaitAsync(Bound);

        Assert.That(released, Is.True);
        lock (ran) Assert.That(ran, Is.EqualTo(["release"]));

        stuck.SetResult();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await parked.WaitAsync(Bound), Is.True);
            Assert.That(await after.WaitAsync(Bound), Is.True);
        }
        lock (ran) Assert.That(ran, Is.EqualTo(["release", "after"]));
    }

    [Test]
    public async Task AnUnansweredCall_TimesOutAndTheLaneMovesOn()
    {
        var time = new StartedTimers();
        var lane = new SerialWork(NullLogger.Instance, time);
        var never = new TaskCompletionSource();
        var timeout = TimeSpan.FromSeconds(5);

        var hung = lane.Enqueue("hung", _ => never.Task, timeout, CancellationToken.None);
        var next = lane.Enqueue("next", _ => Task.CompletedTask, Long, CancellationToken.None);

        var due = await time.Started.Next("the hung call's deadline");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(due, Is.EqualTo(timeout));
            Assert.That(hung.IsCompleted, Is.False, "the deadline has not passed");
        }
        time.Advance(timeout);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await hung.WaitAsync(Bound), Is.False);
            Assert.That(await next.WaitAsync(Bound), Is.True);
        }
    }

    [Test]
    public async Task AFailure_IsReportedAndTheLaneMovesOn()
    {
        var lane = new SerialWork(NullLogger.Instance);

        var failed = lane.Enqueue("fails", _ => Task.FromException(new InvalidOperationException("refused")), Long, CancellationToken.None);
        var next = lane.Enqueue("next", _ => Task.CompletedTask, Long, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await failed.WaitAsync(Bound), Is.False);
            Assert.That(await next.WaitAsync(Bound), Is.True);
        }
    }
}
