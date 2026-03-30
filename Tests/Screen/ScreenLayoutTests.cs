using Hydra.Screen;

namespace Tests.Screen;

[TestFixture]
public class ScreenLayoutTests
{
    private static ScreenRect Main => new("main", 0, 0, 2560, 1440, false);
    private static ScreenRect Right => new("right", 2560, 0, 2560, 1440, true);
    private static ScreenLayout Layout => new([Main, Right]);

    // -- edge detection --

    [Test]
    public void DetectEdgeExit_CursorInMiddle_ReturnsNull()
    {
        var hit = Layout.DetectEdgeExit(Main, 1280, 720);
        Assert.That(hit, Is.Null);
    }

    [Test]
    public void DetectEdgeExit_CursorAtRightEdge_ReturnsVirtualScreen()
    {
        var hit = Layout.DetectEdgeExit(Main, 2559, 720);
        Assert.That(hit, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(hit!.Destination.Name, Is.EqualTo("right"));
            Assert.That(hit.Direction, Is.EqualTo(Direction.Right));
        }
    }

    [Test]
    public void DetectEdgeExit_CursorOnLeftEdge_NoNeighbor_ReturnsNull()
    {
        var hit = Layout.DetectEdgeExit(Main, 0, 720);
        Assert.That(hit, Is.Null);
    }

    [Test]
    public void DetectEdgeExit_CursorOnTopEdge_NoNeighbor_ReturnsNull()
    {
        var hit = Layout.DetectEdgeExit(Main, 1280, 0);
        Assert.That(hit, Is.Null);
    }

    [Test]
    public void DetectEdgeExit_CursorOnBottomEdge_NoNeighbor_ReturnsNull()
    {
        var hit = Layout.DetectEdgeExit(Main, 1280, 1439);
        Assert.That(hit, Is.Null);
    }

    [Test]
    public void DetectEdgeExit_VirtualScreen_LeftEdge_ReturnsMain()
    {
        var hit = Layout.DetectEdgeExit(Right, 2560, 720);
        Assert.That(hit, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(hit!.Destination.Name, Is.EqualTo("main"));
            Assert.That(hit.Direction, Is.EqualTo(Direction.Left));
        }
    }

    // -- coordinate mapping --

    [Test]
    public void DetectEdgeExit_EntryX_NudgedInward()
    {
        // entering the virtual screen from the right edge: entry X should be
        // the left edge of the destination, nudged inward past the jump zone
        var hit = Layout.DetectEdgeExit(Main, 2559, 720);
        Assert.That(hit!.EntryX, Is.GreaterThan(Right.X));
    }

    [Test]
    public void DetectEdgeExit_EntryY_MappedFractionally()
    {
        // cursor at middle of main screen's right edge → should land at middle of right screen's left edge
        var hit = Layout.DetectEdgeExit(Main, 2559, 720);
        Assert.That(hit!.EntryY, Is.EqualTo(720).Within(2));
    }

    [Test]
    public void DetectEdgeExit_EntryY_TopQuarter_MappedCorrectly()
    {
        // cursor at Y=360 (25% down) → should land at 25% down on destination
        var hit = Layout.DetectEdgeExit(Main, 2559, 360);
        Assert.That(hit!.EntryY, Is.EqualTo(360).Within(2));
    }

    [Test]
    public void DetectEdgeExit_ReturningLeft_EntryX_NudgedInward()
    {
        // returning from virtual to main: entry X should be right edge of main, nudged inward
        var hit = Layout.DetectEdgeExit(Right, 2560, 720);
        Assert.That(hit!.EntryX, Is.LessThan(Main.X + Main.Width));
    }
}
