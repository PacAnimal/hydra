using Hydra.Config;
using Hydra.Screen;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Screen;

[TestFixture]
public class ScreenLayoutTests
{
    // two screens: "home" (2560x1440) has "remote" (2560x1440) to the right
    private static ScreenRect Home => new("home", "home", 0, 0, 2560, 1440, IsLocal: true);
    private static ScreenRect Remote => new("remote", "remote", 0, 0, 2560, 1440, IsLocal: false);

    private static ScreenLayout Layout => new(
        [Home, Remote],
        [
            new HostConfig { Name = "home", Neighbours = [new NeighbourConfig { Direction = Direction.Right, Name = "remote" }] },
            new HostConfig { Name = "remote", Neighbours = [new NeighbourConfig { Direction = Direction.Left, Name = "home" }] },
        ],
        null,
        NullLogger.Instance);

    // -- edge detection --

    [Test]
    public void DetectEdgeExit_CursorInMiddle_ReturnsNull()
    {
        var hit = Layout.DetectEdgeExit(Home, 1280, 720);
        Assert.That(hit, Is.Null);
    }

    [Test]
    public void DetectEdgeExit_CursorAtRightEdge_ReturnsRemoteScreen()
    {
        var hit = Layout.DetectEdgeExit(Home, 2559, 720);
        Assert.That(hit, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(hit!.Destination.Name, Is.EqualTo("remote"));
            Assert.That(hit.Direction, Is.EqualTo(Direction.Right));
        }
    }

    [Test]
    public void DetectEdgeExit_CursorOnLeftEdge_NoNeighbor_ReturnsNull()
    {
        var hit = Layout.DetectEdgeExit(Home, 0, 720);
        Assert.That(hit, Is.Null);
    }

    [Test]
    public void DetectEdgeExit_CursorOnTopEdge_NoNeighbor_ReturnsNull()
    {
        var hit = Layout.DetectEdgeExit(Home, 1280, 0);
        Assert.That(hit, Is.Null);
    }

    [Test]
    public void DetectEdgeExit_CursorOnBottomEdge_NoNeighbor_ReturnsNull()
    {
        var hit = Layout.DetectEdgeExit(Home, 1280, 1439);
        Assert.That(hit, Is.Null);
    }

    [Test]
    public void DetectEdgeExit_RemoteScreen_LeftEdge_ReturnsHome()
    {
        var hit = Layout.DetectEdgeExit(Remote, 0, 720);
        Assert.That(hit, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(hit!.Destination.Name, Is.EqualTo("home"));
            Assert.That(hit.Direction, Is.EqualTo(Direction.Left));
        }
    }

    // -- coordinate mapping --

    [Test]
    public void DetectEdgeExit_EntryX_NudgedInward()
    {
        // entering remote screen from right edge: entryX should be nudged past left edge
        var hit = Layout.DetectEdgeExit(Home, 2559, 720);
        Assert.That(hit!.EntryX, Is.GreaterThan(0));
    }

    [Test]
    public void DetectEdgeExit_EntryY_MappedFractionally()
    {
        // cursor at middle of right edge → lands at middle of destination
        var hit = Layout.DetectEdgeExit(Home, 2559, 720);
        Assert.That(hit!.EntryY, Is.EqualTo(720).Within(2));
    }

    [Test]
    public void DetectEdgeExit_EntryY_TopQuarter_MappedCorrectly()
    {
        // cursor at 25% down → lands at 25% down on destination
        var hit = Layout.DetectEdgeExit(Home, 2559, 360);
        Assert.That(hit!.EntryY, Is.EqualTo(360).Within(2));
    }

    [Test]
    public void DetectEdgeExit_ReturningLeft_EntryX_NudgedInward()
    {
        // returning home: entryX should be nudged away from right edge
        var hit = Layout.DetectEdgeExit(Remote, 0, 720);
        Assert.That(hit!.EntryX, Is.LessThan(Home.Width - 1));
    }

    // -- skip-through offline screens --

    [Test]
    public void DetectEdgeExit_SkipsOfflineScreen_ReachesLiveScreen()
    {
        // A → B (offline, Width=0) → C (live, 1920x1080)
        var a = new ScreenRect("a", "a", 0, 0, 2560, 1440, IsLocal: true);
        var b = new ScreenRect("b", "b", 0, 0, 0, 0, IsLocal: false);      // offline
        var c = new ScreenRect("c", "c", 0, 0, 1920, 1080, IsLocal: false);

        var layout = new ScreenLayout(
            [a, b, c],
            [
                new HostConfig { Name = "a", Neighbours = [new NeighbourConfig { Direction = Direction.Right, Name = "b" }] },
                new HostConfig { Name = "b", Neighbours = [new NeighbourConfig { Direction = Direction.Right, Name = "c" }] },
                new HostConfig { Name = "c", Neighbours = [] },
            ],
            null,
            NullLogger.Instance);

        var hit = layout.DetectEdgeExit(a, 2559, 720);
        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.Destination.Name, Is.EqualTo("c"));
    }

    [Test]
    public void DetectEdgeExit_AllOffline_ReturnsNull()
    {
        // A → B (offline) → C (offline) — dead end
        var a = new ScreenRect("a", "a", 0, 0, 2560, 1440, IsLocal: true);
        var b = new ScreenRect("b", "b", 0, 0, 0, 0, IsLocal: false);
        var c = new ScreenRect("c", "c", 0, 0, 0, 0, IsLocal: false);

        var layout = new ScreenLayout(
            [a, b, c],
            [
                new HostConfig { Name = "a", Neighbours = [new NeighbourConfig { Direction = Direction.Right, Name = "b" }] },
                new HostConfig { Name = "b", Neighbours = [new NeighbourConfig { Direction = Direction.Right, Name = "c" }] },
                new HostConfig { Name = "c", Neighbours = [] },
            ],
            null,
            NullLogger.Instance);

        var hit = layout.DetectEdgeExit(a, 2559, 720);
        Assert.That(hit, Is.Null);
    }

    // -- range-based mapping --

    [Test]
    public void DetectEdgeExit_RangeBased_CursorInSourceRange_MapsToDestRange()
    {
        // source: top 50% (0-50%) of right edge → dest: bottom 50% (50-100%)
        var home = new ScreenRect("home", "home", 0, 0, 2560, 1440, IsLocal: true);
        var remote = new ScreenRect("remote", "remote", 0, 0, 2560, 1440, IsLocal: false);

        var layout = new ScreenLayout(
            [home, remote],
            [new HostConfig
            {
                Name = "home",
                Neighbours = [new NeighbourConfig
                {
                    Direction = Direction.Right, Name = "remote",
                    SourceStart = 0, SourceEnd = 50,
                    DestStart = 50, DestEnd = 100,
                }],
            }],
            null,
            NullLogger.Instance);

        // cursor at 25% down (360px of 1440) → should map to 75% down on dest (75% of 1440 = 1080)
        var hit = layout.DetectEdgeExit(home, 2559, 360);
        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.EntryY, Is.EqualTo(1080).Within(5));
    }

    [Test]
    public void DetectEdgeExit_RangeBased_CursorOutsideSourceRange_ReturnsNull()
    {
        // source: bottom 50% (50-100%) of right edge only
        var home = new ScreenRect("home", "home", 0, 0, 2560, 1440, IsLocal: true);
        var remote = new ScreenRect("remote", "remote", 0, 0, 2560, 1440, IsLocal: false);

        var layout = new ScreenLayout(
            [home, remote],
            [new HostConfig
            {
                Name = "home",
                Neighbours = [new NeighbourConfig
                {
                    Direction = Direction.Right, Name = "remote",
                    SourceStart = 50, SourceEnd = 100,
                }],
            }],
            null,
            NullLogger.Instance);

        // cursor in top 25% — outside the source range
        var hit = layout.DetectEdgeExit(home, 2559, 200);
        Assert.That(hit, Is.Null);
    }

    [Test]
    public void DetectEdgeExit_SplitEdge_RoutesToCorrectHost()
    {
        // right edge split: top half → hostB, bottom half → hostC
        var home = new ScreenRect("home", "home", 0, 0, 2560, 1440, IsLocal: true);
        var hostB = new ScreenRect("hostB", "hostB", 0, 0, 2560, 1440, IsLocal: false);
        var hostC = new ScreenRect("hostC", "hostC", 0, 0, 2560, 1440, IsLocal: false);

        var layout = new ScreenLayout(
            [home, hostB, hostC],
            [new HostConfig
            {
                Name = "home",
                Neighbours =
                [
                    new NeighbourConfig { Direction = Direction.Right, Name = "hostB", SourceStart = 0, SourceEnd = 50 },
                    new NeighbourConfig { Direction = Direction.Right, Name = "hostC", SourceStart = 50, SourceEnd = 100 },
                ],
            }],
            null,
            NullLogger.Instance);

        var topHit = layout.DetectEdgeExit(home, 2559, 200);   // top 14% → hostB
        var bottomHit = layout.DetectEdgeExit(home, 2559, 1200); // bottom 83% → hostC

        using (Assert.EnterMultipleScope())
        {
            Assert.That(topHit?.Destination.Name, Is.EqualTo("hostB"));
            Assert.That(bottomHit?.Destination.Name, Is.EqualTo("hostC"));
        }
    }

    [Test]
    public void DetectEdgeExit_FullRangeDefault_WorksLikeBeforeRangeBased()
    {
        // neighbours with no explicit ranges should behave identically to the original full-edge mapping
        var hit = Layout.DetectEdgeExit(Home, 2559, 720);
        Assert.That(hit, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(hit!.Destination.Name, Is.EqualTo("remote"));
            Assert.That(hit.EntryY, Is.EqualTo(720).Within(2));
        }
    }

    // -- vertical edge range-based mapping --

    [Test]
    public void DetectEdgeExit_RangeBased_BottomEdge_MapsToDestRange()
    {
        // bottom edge: right 50% (50-100%) → dest top 25% (0-25%)
        var top = new ScreenRect("top", "top", 0, 0, 1920, 1080, IsLocal: true);
        var below = new ScreenRect("below", "below", 0, 0, 1920, 1080, IsLocal: false);

        var layout = new ScreenLayout(
            [top, below],
            [new HostConfig
            {
                Name = "top",
                Neighbours = [new NeighbourConfig
                {
                    Direction = Direction.Down, Name = "below",
                    SourceStart = 50, SourceEnd = 100,
                    DestStart = 0, DestEnd = 25,
                }],
            }],
            null,
            NullLogger.Instance);

        // cursor in right 75% (1440px of 1920) — within source range (50-100%)
        var hit = layout.DetectEdgeExit(top, 1440, 1079);
        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.Destination.Name, Is.EqualTo("below"));

        // cursor in left 25% (240px) — outside source range, returns null
        var miss = layout.DetectEdgeExit(top, 240, 1079);
        Assert.That(miss, Is.Null);
    }

    [Test]
    public void DetectEdgeExit_RangeBased_TopEdge_MapsToDestRange()
    {
        // top edge: left 50% (0-50%) → dest right 50% (50-100%)
        var bottom = new ScreenRect("bottom", "bottom", 0, 1080, 1920, 1080, IsLocal: true);
        var above = new ScreenRect("above", "above", 0, 0, 1920, 1080, IsLocal: false);

        var layout = new ScreenLayout(
            [bottom, above],
            [new HostConfig
            {
                Name = "bottom",
                Neighbours = [new NeighbourConfig
                {
                    Direction = Direction.Up, Name = "above",
                    SourceStart = 0, SourceEnd = 50,
                    DestStart = 50, DestEnd = 100,
                }],
            }],
            null,
            NullLogger.Instance);

        // cursor at x=400 (left 20%, within 0-50% range) → maps to right half of dest
        var hit = layout.DetectEdgeExit(bottom, 400, 0);
        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.EntryX, Is.GreaterThanOrEqualTo(960)); // dest right half starts at 960

        // cursor at x=1600 (right 83%, outside 0-50% range) → null
        var miss = layout.DetectEdgeExit(bottom, 1600, 0);
        Assert.That(miss, Is.Null);
    }

    // -- maxHops ceiling --

    [Test]
    public void DetectEdgeExit_MaxHops_ReturnsNull_After10OfflineScreens()
    {
        // chain of 11 offline screens — should stop at maxHops=10 and return null
        var screens = new List<ScreenRect>
        {
            new("a", "a", 0, 0, 2560, 1440, IsLocal: true),
        };
        var configs = new List<HostConfig>();

        for (var i = 0; i < 11; i++)
        {
            var name = $"offline{i}";
            screens.Add(new ScreenRect(name, name, 0, 0, 0, 0, IsLocal: false));
        }

        // build chain: a → offline0 → offline1 → ... → offline10
        configs.Add(new HostConfig { Name = "a", Neighbours = [new NeighbourConfig { Direction = Direction.Right, Name = "offline0" }] });
        for (var i = 0; i < 10; i++)
            configs.Add(new HostConfig { Name = $"offline{i}", Neighbours = [new NeighbourConfig { Direction = Direction.Right, Name = $"offline{i + 1}" }] });
        configs.Add(new HostConfig { Name = "offline10", Neighbours = [] });

        var layout = new ScreenLayout(screens, configs, null, NullLogger.Instance);
        var hit = layout.DetectEdgeExit(screens[0], 2559, 720);
        Assert.That(hit, Is.Null);
    }

    // -- dead corners --

    private static ScreenLayout DeadCornersLayout(int? rootDeadCorners = null, int? hostDeadCorners = null)
    {
        return new ScreenLayout(
            [Home, Remote],
            [
                new HostConfig { Name = "home", DeadCorners = hostDeadCorners, Neighbours = [new NeighbourConfig { Direction = Direction.Right, Name = "remote" }] },
                new HostConfig { Name = "remote", Neighbours = [new NeighbourConfig { Direction = Direction.Left, Name = "home" }] },
            ],
            rootDeadCorners,
            NullLogger.Instance);
    }

    [Test]
    public void DeadCorners_Zero_NoCornersBlocked()
    {
        // deadCorners=0 (default) — cursor at top corner still transitions
        var layout = DeadCornersLayout(rootDeadCorners: 0);
        var hit = layout.DetectEdgeExit(Home, 2559, 0);
        Assert.That(hit, Is.Not.Null);
    }

    [Test]
    public void DeadCorners_BlocksNearTopCorner()
    {
        // deadCorners=10 — cursor at ~3% from top of right edge is blocked
        var layout = DeadCornersLayout(rootDeadCorners: 10);
        var hit = layout.DetectEdgeExit(Home, 2559, 40);  // 40/1440 ≈ 2.8%
        Assert.That(hit, Is.Null);
    }

    [Test]
    public void DeadCorners_BlocksNearBottomCorner()
    {
        // deadCorners=10 — cursor at ~97% from top of right edge is blocked
        var layout = DeadCornersLayout(rootDeadCorners: 10);
        var hit = layout.DetectEdgeExit(Home, 2559, 1400);  // 1400/1440 ≈ 97.3%
        Assert.That(hit, Is.Null);
    }

    [Test]
    public void DeadCorners_AllowsMiddle()
    {
        // deadCorners=10 — cursor at 50% transitions normally
        var layout = DeadCornersLayout(rootDeadCorners: 10);
        var hit = layout.DetectEdgeExit(Home, 2559, 720);
        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.Destination.Name, Is.EqualTo("remote"));
    }

    [Test]
    public void DeadCorners_PerHost_OverridesRoot()
    {
        // root=0, host=20 — cursor at ~3% should be blocked by host setting
        var layout = DeadCornersLayout(rootDeadCorners: 0, hostDeadCorners: 20);
        var hit = layout.DetectEdgeExit(Home, 2559, 40);  // ~2.8%, within host's 20% dead zone
        Assert.That(hit, Is.Null);
    }

    [Test]
    public void DeadCorners_PerHost_NullInheritsRoot()
    {
        // root=15, host has no override (null) — cursor near top corner should be blocked
        var layout = DeadCornersLayout(rootDeadCorners: 15);
        var hit = layout.DetectEdgeExit(Home, 2559, 40);  // ~2.8%, within root's 15% dead zone
        Assert.That(hit, Is.Null);
    }

    [Test]
    public void DeadCorners_HorizontalEdge_BlocksNearCorners()
    {
        // deadCorners=10 on a bottom edge — corners are at left and right ends
        var top = new ScreenRect("top", "top", 0, 0, 1920, 1080, IsLocal: true);
        var below = new ScreenRect("below", "below", 0, 0, 1920, 1080, IsLocal: false);

        var layout = new ScreenLayout(
            [top, below],
            [new HostConfig
            {
                Name = "top",
                DeadCorners = 10,
                Neighbours = [new NeighbourConfig { Direction = Direction.Down, Name = "below" }],
            }],
            null,
            NullLogger.Instance);

        // cursor near left corner (~2.6% along bottom edge) — blocked
        var leftCorner = layout.DetectEdgeExit(top, 50, 1079);
        // cursor in middle (~50%) — allowed
        var middle = layout.DetectEdgeExit(top, 960, 1079);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(leftCorner, Is.Null);
            Assert.That(middle, Is.Not.Null);
        }
    }

    [Test]
    public void DeadCorners_InboundNotBlocked()
    {
        // host "home" has deadCorners=20, but transitions INTO home from remote should still work.
        // dead corners only blocks outbound transitions (leaving the current host).
        var layout = DeadCornersLayout(hostDeadCorners: 20);

        // cursor on remote at left edge near top corner (y=40, ~2.8%) — returning to home
        // remote has no deadCorners, so its outbound transition is fine
        var hit = layout.DetectEdgeExit(Remote, 0, 40);
        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.Destination.Name, Is.EqualTo("home"));
    }
}
