using Hydra.Config;
using Hydra.Screen;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Screen;

[TestFixture]
public class CrossableSpansTests
{
    private static ScreenRect Home => new("home", "home", 0, 0, 2560, 1440, IsLocal: true);
    private static ScreenRect Remote => new("remote", "remote", 0, 0, 1920, 1080, IsLocal: false);
    private static ScreenRect Other => new("other", "other", 0, 0, 1920, 1080, IsLocal: false);
    private static ScreenRect Offline => new("remote", "remote", 0, 0, 0, 0, IsLocal: false);

    private static ScreenLayout Layout(List<ScreenRect> screens, int? deadCorners, params NeighbourConfig[] neighbours) => new(
        [Home, .. screens],
        [new HostConfig { Name = "home", Neighbours = [.. neighbours] }],
        deadCorners,
        [],
        NullLogger.Instance);

    [Test]
    public void FullEdgeLink_IsTheWholeEdgeBarItsCorners()
    {
        var layout = Layout([Remote], null, new NeighbourConfig { Direction = Direction.Up, Name = "remote" });

        // a corner pixel belongs to the side edge, so the top edge loses both ends
        Assert.That(layout.CrossableSpans(Home), Is.EqualTo([new EdgeSpan(Direction.Up, 1, 2559)]));
        AssertAgreesWithDetectEdgeExit(layout);
    }

    [Test]
    public void FullSideEdgeLink_IsTheWholeEdge()
    {
        var layout = Layout([Remote], null, new NeighbourConfig { Direction = Direction.Right, Name = "remote" });

        Assert.That(layout.CrossableSpans(Home), Is.EqualTo([new EdgeSpan(Direction.Right, 0, 1440)]));
        AssertAgreesWithDetectEdgeExit(layout);
    }

    [Test]
    public void PartialLink_IsItsSourceRange()
    {
        var layout = Layout([Remote], null,
            new NeighbourConfig { Direction = Direction.Right, Name = "remote", SourceStart = 25, SourceEnd = 75 });

        Assert.That(layout.CrossableSpans(Home), Has.Count.EqualTo(1));
        AssertAgreesWithDetectEdgeExit(layout);
    }

    [Test]
    public void TwoLinksOnOneEdge_WithAGap_AreTwoSpans()
    {
        var layout = Layout([Remote, Other], null,
            new NeighbourConfig { Direction = Direction.Down, Name = "remote", SourceStart = 0, SourceEnd = 30 },
            new NeighbourConfig { Direction = Direction.Down, Name = "other", SourceStart = 60, SourceEnd = 100 });

        Assert.That(layout.CrossableSpans(Home), Has.Count.EqualTo(2));
        AssertAgreesWithDetectEdgeExit(layout);
    }

    [Test]
    public void TwoLinksOnOneEdge_Touching_AreOneSpan()
    {
        var layout = Layout([Remote, Other], null,
            new NeighbourConfig { Direction = Direction.Left, Name = "remote", SourceStart = 0, SourceEnd = 50 },
            new NeighbourConfig { Direction = Direction.Left, Name = "other", SourceStart = 50, SourceEnd = 100 });

        Assert.That(layout.CrossableSpans(Home), Is.EqualTo([new EdgeSpan(Direction.Left, 0, 1440)]));
        AssertAgreesWithDetectEdgeExit(layout);
    }

    [Test]
    public void DeadCorners_TakeBothEndsOff()
    {
        var layout = Layout([Remote], 144, new NeighbourConfig { Direction = Direction.Right, Name = "remote" });

        Assert.That(layout.CrossableSpans(Home), Is.EqualTo([new EdgeSpan(Direction.Right, 144, 1440 - 144)]));
        AssertAgreesWithDetectEdgeExit(layout);
    }

    [Test]
    public void DeadCorners_OnPartialLinks()
    {
        var layout = Layout([Remote, Other], 300,
            new NeighbourConfig { Direction = Direction.Up, Name = "remote", SourceStart = 0, SourceEnd = 40 },
            new NeighbourConfig { Direction = Direction.Right, Name = "other", SourceStart = 70, SourceEnd = 100 });

        AssertAgreesWithDetectEdgeExit(layout);
    }

    [Test]
    public void OfflineDestination_IsNoSpan()
    {
        var layout = Layout([Offline], null, new NeighbourConfig { Direction = Direction.Right, Name = "remote" });

        Assert.That(layout.CrossableSpans(Home), Is.Empty);
        AssertAgreesWithDetectEdgeExit(layout);
    }

    [Test]
    public void OfflineDestination_BesideAnOnlineOne_LeavesOnlyTheOnline()
    {
        var layout = Layout([Offline, Other], null,
            new NeighbourConfig { Direction = Direction.Right, Name = "remote", SourceStart = 0, SourceEnd = 50 },
            new NeighbourConfig { Direction = Direction.Right, Name = "other", SourceStart = 50, SourceEnd = 100 });

        Assert.That(layout.CrossableSpans(Home), Has.Count.EqualTo(1));
        AssertAgreesWithDetectEdgeExit(layout);
    }

    [Test]
    public void NoNeighbour_IsNoSpan()
    {
        var layout = Layout([Remote], null);

        Assert.That(layout.CrossableSpans(Home), Is.Empty);
        AssertAgreesWithDetectEdgeExit(layout);
    }

    // every pixel of every edge of Home: in a span of that edge iff DetectEdgeExit crosses there by that edge
    private static void AssertAgreesWithDetectEdgeExit(ScreenLayout layout)
    {
        var home = Home;
        var spans = layout.CrossableSpans(home);
        var disagreements = new List<string>();
        foreach (var dir in Enum.GetValues<Direction>())
        {
            var edgeLen = dir is Direction.Left or Direction.Right ? home.Height : home.Width;
            for (var p = 0; p < edgeLen; p++)
            {
                var at = dir switch
                {
                    Direction.Left => new ScreenPoint(0, p),
                    Direction.Right => new ScreenPoint(home.Width - 1, p),
                    Direction.Up => new ScreenPoint(p, 0),
                    _ => new ScreenPoint(p, home.Height - 1),
                };
                var crosses = layout.DetectEdgeExit(home, at.X, at.Y)?.Direction == dir;
                var inSpan = spans.Any(s => s.Direction == dir && p >= s.Start && p < s.End);
                if (inSpan != crosses) disagreements.Add($"{dir} {p}: span {inSpan}, DetectEdgeExit {crosses}");
            }
        }
        Assert.That(disagreements, Is.Empty);
    }
}
