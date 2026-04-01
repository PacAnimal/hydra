using Hydra.Config;
using Hydra.Screen;

namespace Tests.Screen;

[TestFixture]
public class ScreenLayoutTests
{
    // two screens: "home" (2560x1440) has "remote" (2560x1440) to the right
    private static ScreenRect Home => new("home", 2560, 1440);
    private static ScreenRect Remote => new("remote", 2560, 1440, true);

    private static ScreenLayout Layout => new(
        [Home, Remote],
        [
            new ScreenConfig { Name = "home", Neighbours = [new NeighbourConfig { Direction = Direction.Right, Name = "remote" }] },
            new ScreenConfig { Name = "remote", Neighbours = [new NeighbourConfig { Direction = Direction.Left, Name = "home" }] },
        ]);

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

    // -- scale --

    [Test]
    public void DetectEdgeExit_Scale_ReturnedInHit()
    {
        var layout = new ScreenLayout(
            [Home, Remote],
            [
                new ScreenConfig { Name = "home", Neighbours = [new NeighbourConfig { Direction = Direction.Right, Name = "remote", Scale = 0.75m }] },
                new ScreenConfig { Name = "remote", Neighbours = [] },
            ]);

        var hit = layout.DetectEdgeExit(Home, 2559, 720);
        Assert.That(hit!.Scale, Is.EqualTo(0.75m));
    }

    // -- offset --

    [Test]
    public void DetectEdgeExit_PositiveOffset_ShiftsEntryDown()
    {
        // offset=50 on a right-exit should move entry point 50% of destination height downward
        var layout = new ScreenLayout(
            [Home, Remote],
            [
                new ScreenConfig { Name = "home", Neighbours = [new NeighbourConfig { Direction = Direction.Right, Name = "remote", Offset = 50 }] },
                new ScreenConfig { Name = "remote", Neighbours = [] },
            ]);

        var baseline = Layout.DetectEdgeExit(Home, 2559, 720);      // no offset
        var shifted = layout.DetectEdgeExit(Home, 2559, 720);       // +50% offset

        Assert.That(shifted!.EntryY, Is.GreaterThan(baseline!.EntryY));
    }

    [Test]
    public void DetectEdgeExit_NegativeOffset_ShiftsEntryUp()
    {
        var layout = new ScreenLayout(
            [Home, Remote],
            [
                new ScreenConfig { Name = "home", Neighbours = [new NeighbourConfig { Direction = Direction.Right, Name = "remote", Offset = -50 }] },
                new ScreenConfig { Name = "remote", Neighbours = [] },
            ]);

        var baseline = Layout.DetectEdgeExit(Home, 2559, 720);
        var shifted = layout.DetectEdgeExit(Home, 2559, 720);

        Assert.That(shifted!.EntryY, Is.LessThan(baseline!.EntryY));
    }

    // -- skip-through offline screens --

    [Test]
    public void DetectEdgeExit_SkipsOfflineScreen_ReachesLiveScreen()
    {
        // A → B (offline, Width=0) → C (live, 1920x1080)
        var a = new ScreenRect("a", 2560, 1440);
        var b = new ScreenRect("b", 0, 0, true);      // offline
        var c = new ScreenRect("c", 1920, 1080, true);

        var layout = new ScreenLayout(
            [a, b, c],
            [
                new ScreenConfig { Name = "a", Neighbours = [new NeighbourConfig { Direction = Direction.Right, Name = "b" }] },
                new ScreenConfig { Name = "b", Neighbours = [new NeighbourConfig { Direction = Direction.Right, Name = "c" }] },
                new ScreenConfig { Name = "c", Neighbours = [] },
            ]);

        var hit = layout.DetectEdgeExit(a, 2559, 720);
        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.Destination.Name, Is.EqualTo("c"));
    }

    [Test]
    public void DetectEdgeExit_AllOffline_ReturnsNull()
    {
        // A → B (offline) → C (offline) — dead end
        var a = new ScreenRect("a", 2560, 1440);
        var b = new ScreenRect("b", 0, 0, true);
        var c = new ScreenRect("c", 0, 0, true);

        var layout = new ScreenLayout(
            [a, b, c],
            [
                new ScreenConfig { Name = "a", Neighbours = [new NeighbourConfig { Direction = Direction.Right, Name = "b" }] },
                new ScreenConfig { Name = "b", Neighbours = [new NeighbourConfig { Direction = Direction.Right, Name = "c" }] },
                new ScreenConfig { Name = "c", Neighbours = [] },
            ]);

        var hit = layout.DetectEdgeExit(a, 2559, 720);
        Assert.That(hit, Is.Null);
    }
}
