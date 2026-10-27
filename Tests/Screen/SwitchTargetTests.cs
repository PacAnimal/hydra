using Hydra.Config;
using Hydra.Screen;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Screen;

// which screen the monitor switch hotkey picks: the one on the pressed edge, or of several the closest to the middle of
// that edge, or of its end third a second, perpendicular key names; a tie going left or up
[TestFixture]
public class SwitchTargetTests
{
    // "pc:0" is a 1920x1080 screen at (0, 1080), with whatever else a test puts on pc around it
    private static readonly ScreenRect Below = Pc(0, 0, 1080, 1920, 1080);

    [TestCase(null, "pc:1")]
    [TestCase(Direction.Left, "pc:1")]
    [TestCase(Direction.Right, "pc:2")]
    public void TwoScreensAbove_AnEvenSplit_TieGoesLeft(Direction? third, string expected)
    {
        var layout = SameHost(Below, Pc(1, 0, 0, 960, 1080), Pc(2, 960, 0, 960, 1080));

        Assert.That(layout.SwitchTarget(Below, Direction.Up, third, includeLocal: true)?.Name, Is.EqualTo(expected));
    }

    [TestCase(null, "pc:2")]
    [TestCase(Direction.Left, "pc:1")]
    [TestCase(Direction.Right, "pc:3")]
    public void ThreeScreensAbove_EachThirdReachesItsOwn(Direction? third, string expected)
    {
        var layout = SameHost(Below, Pc(1, 0, 0, 640, 1080), Pc(2, 640, 0, 640, 1080), Pc(3, 1280, 0, 640, 1080));

        Assert.That(layout.SwitchTarget(Below, Direction.Up, third, includeLocal: true)?.Name, Is.EqualTo(expected));
    }

    // only one screen that way: every key sequence starting there goes to it, wherever it sits along the edge
    [TestCase(null)]
    [TestCase(Direction.Left)]
    [TestCase(Direction.Right)]
    public void OneScreenAbove_InTheUpperLeft_IsReachedFromAnyThird(Direction? third)
    {
        var layout = SameHost(Below, Pc(1, 0, 0, 400, 1080));

        Assert.That(layout.SwitchTarget(Below, Direction.Up, third, includeLocal: true)?.Name, Is.EqualTo("pc:1"));
    }

    // neither covers the middle: the nearer to it wins
    [Test]
    public void NoScreenOnTheAimedPoint_TheNearestWins()
    {
        var layout = SameHost(Below, Pc(1, 0, 0, 384, 1080), Pc(2, 1152, 0, 768, 1080));

        Assert.That(layout.SwitchTarget(Below, Direction.Up, null, includeLocal: true)?.Name, Is.EqualTo("pc:2"));
    }

    [Test]
    public void TwoScreensBesideIt_AnEvenSplit_TieGoesUp()
    {
        var left = Pc(0, 1920, 0, 1920, 1080);
        var layout = SameHost(left, Pc(1, 0, 0, 1920, 540), Pc(2, 0, 540, 1920, 540));

        Assert.That(layout.SwitchTarget(left, Direction.Left, null, includeLocal: true)?.Name, Is.EqualTo("pc:1"));
    }

    [TestCase(Direction.Up)]
    [TestCase(Direction.Down)]
    public void ASecondKeyAlongTheEdge_GoesNowhere(Direction third)
    {
        var layout = SameHost(Below, Pc(1, 0, 0, 1920, 1080));

        Assert.That(layout.SwitchTarget(Below, Direction.Up, third, includeLocal: true), Is.Null);
    }

    [Test]
    public void NothingThatWay_GoesNowhere()
    {
        var layout = SameHost(Below, Pc(1, 0, 0, 1920, 1080));

        Assert.That(layout.SwitchTarget(Below, Direction.Down, null, includeLocal: true), Is.Null);
    }

    // a corner touch, or a screen beyond a gap, is not across the edge
    [Test]
    public void AScreenOnlyTouchingACorner_IsNotANeighbour()
    {
        var layout = SameHost(Below, Pc(1, 1920, 0, 1920, 1080));

        Assert.That(layout.SwitchTarget(Below, Direction.Up, null, includeLocal: true), Is.Null);
    }

    // another host's screens come from the configured links, over the part of the edge each covers
    [TestCase(null, "upper")]
    [TestCase(Direction.Up, "upper")]
    [TestCase(Direction.Down, "lower")]
    public void OtherHosts_AreReachedThroughTheirLinks(Direction? third, string expected)
    {
        var home = new ScreenRect("home", "home", 0, 0, 1920, 1080, IsLocal: true);
        var layout = new ScreenLayout(
            [home, Remote("upper"), Remote("lower")],
            [
                new HostConfig
                {
                    Name = "home",
                    Neighbours =
                    [
                        new NeighbourConfig { Direction = Direction.Right, Name = "upper", SourceEnd = 50 },
                        new NeighbourConfig { Direction = Direction.Right, Name = "lower", SourceStart = 50 },
                    ],
                },
            ],
            null, [], NullLogger.Instance);

        Assert.That(layout.SwitchTarget(home, Direction.Right, third, includeLocal: true)?.Name, Is.EqualTo(expected));
    }

    [Test]
    public void AnOfflineHost_IsSkippedToTheNextOneThatWay()
    {
        var home = new ScreenRect("home", "home", 0, 0, 1920, 1080, IsLocal: true);
        var offline = new ScreenRect("asleep", "asleep", 0, 0, 0, 0, IsLocal: false);
        var layout = new ScreenLayout(
            [home, offline, Remote("beyond")],
            [
                new HostConfig { Name = "home", Neighbours = [new NeighbourConfig { Direction = Direction.Right, Name = "asleep" }] },
                new HostConfig { Name = "asleep", Neighbours = [new NeighbourConfig { Direction = Direction.Right, Name = "beyond" }] },
            ],
            null, [], NullLogger.Instance);

        Assert.That(layout.SwitchTarget(home, Direction.Right, null, includeLocal: true)?.Name, Is.EqualTo("beyond"));
    }

    // a remote-only master never lands on its own screens
    [Test]
    public void LocalScreens_AreLeftOutWhenNotIncluded()
    {
        var remote = new ScreenRect("remote:0", "remote", 0, 1080, 1920, 1080, IsLocal: false);
        var layout = new ScreenLayout(
            [remote, new ScreenRect("remote:1", "remote", 0, 0, 1920, 1080, IsLocal: false), new ScreenRect("home", "home", 0, 0, 1920, 1080, IsLocal: true)],
            [new HostConfig { Name = "remote", Neighbours = [new NeighbourConfig { Direction = Direction.Left, Name = "home" }] }],
            null, [], NullLogger.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(layout.SwitchTarget(remote, Direction.Left, null, includeLocal: false), Is.Null);
            Assert.That(layout.SwitchTarget(remote, Direction.Left, null, includeLocal: true)?.Name, Is.EqualTo("home"));
            Assert.That(layout.SwitchTarget(remote, Direction.Up, null, includeLocal: false)?.Name, Is.EqualTo("remote:1"));
        }
    }

    // a same-host monitor and another host share the edge: both are candidates
    [TestCase(Direction.Left, "pc:1")]
    [TestCase(Direction.Right, "remote")]
    public void SameHostAndAnotherHostOnOneEdge_AreBothCandidates(Direction third, string expected)
    {
        var layout = new ScreenLayout(
            [Below, Pc(1, 0, 0, 960, 1080), Remote("remote")],
            [new HostConfig { Name = "pc", Neighbours = [new NeighbourConfig { Direction = Direction.Up, Name = "remote", SourceScreen = "pc:0", SourceStart = 50 }] }],
            null, [], NullLogger.Instance);

        Assert.That(layout.SwitchTarget(Below, Direction.Up, third, includeLocal: true)?.Name, Is.EqualTo(expected));
    }

    private static ScreenRect Pc(int index, int x, int y, int width, int height) => new($"pc:{index}", "pc", x, y, width, height, IsLocal: true);

    private static ScreenRect Remote(string host) => new(host, host, 0, 0, 1920, 1080, IsLocal: false);

    private static ScreenLayout SameHost(params ScreenRect[] screens) => new([.. screens], [], null, [], NullLogger.Instance);
}
