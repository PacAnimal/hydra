using Hydra.Config;
using Hydra.Screen;

namespace Tests.Config;

[TestFixture]
public class MirrorTests
{
    private static HostConfig Host(string name, params NeighbourConfig[] neighbours) =>
        new() { Name = name, Neighbours = [.. neighbours] };

    private static NeighbourConfig N(Direction dir, string name, bool mirror = true,
        int srcStart = 0, int srcEnd = 100, int dstStart = 0, int dstEnd = 100,
        string? sourceScreen = null, string? destScreen = null) =>
        new()
        {
            Direction = dir,
            Name = name,
            Mirror = mirror,
            SourceStart = srcStart,
            SourceEnd = srcEnd,
            DestStart = dstStart,
            DestEnd = dstEnd,
            SourceScreen = sourceScreen,
            DestScreen = destScreen,
        };

    // -- basic mirroring --

    [Test]
    public void Mirror_Default_CreatesReverseNeighbour()
    {
        var hosts = new List<HostConfig>
        {
            Host("a", N(Direction.Right, "b")),
            Host("b"),
        };

        HydraConfig.ExpandMirrors(hosts);

        var bNeighbours = hosts.First(h => h.Name == "b").Neighbours;
        Assert.That(bNeighbours, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bNeighbours[0].Direction, Is.EqualTo(Direction.Left));
            Assert.That(bNeighbours[0].Name, Is.EqualTo("a"));
        }
    }

    [Test]
    public void Mirror_False_DoesNotCreateReverse()
    {
        var hosts = new List<HostConfig>
        {
            Host("a", N(Direction.Right, "b", mirror: false)),
            Host("b"),
        };

        HydraConfig.ExpandMirrors(hosts);

        Assert.That(hosts.First(h => h.Name == "b").Neighbours, Is.Empty);
    }

    [Test]
    public void Mirror_ExplicitReverseNotOverwritten()
    {
        // b already has a manual left→a entry; mirror should not add another
        var hosts = new List<HostConfig>
        {
            Host("a", N(Direction.Right, "b")),
            Host("b", N(Direction.Left, "a", srcStart: 10, srcEnd: 90)),
        };

        HydraConfig.ExpandMirrors(hosts);

        var bNeighbours = hosts.First(h => h.Name == "b").Neighbours;
        Assert.That(bNeighbours, Has.Count.EqualTo(1));
        // the manually specified one should be untouched
        Assert.That(bNeighbours[0].SourceStart, Is.EqualTo(10));
    }

    // -- range swapping --

    [Test]
    public void Mirror_RangesAreSwapped()
    {
        // a→right→b: src=0-99, dst=40-97
        // mirror should create b→left→a: src=40-97, dst=0-99
        var hosts = new List<HostConfig>
        {
            Host("a", N(Direction.Right, "b", srcStart: 0, srcEnd: 99, dstStart: 40, dstEnd: 97)),
            Host("b"),
        };

        HydraConfig.ExpandMirrors(hosts);

        var mirror = hosts.First(h => h.Name == "b").Neighbours[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(mirror.SourceStart, Is.EqualTo(40));
            Assert.That(mirror.SourceEnd, Is.EqualTo(97));
            Assert.That(mirror.DestStart, Is.Zero);
            Assert.That(mirror.DestEnd, Is.EqualTo(99));
        }
    }

    // -- screen swapping --

    [Test]
    public void Mirror_ScreensAreSwapped()
    {
        var hosts = new List<HostConfig>
        {
            Host("a", N(Direction.Right, "b", sourceScreen: "screenA", destScreen: "screenB")),
            Host("b"),
        };

        HydraConfig.ExpandMirrors(hosts);

        var mirror = hosts.First(h => h.Name == "b").Neighbours[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(mirror.SourceScreen, Is.EqualTo("screenB"));
            Assert.That(mirror.DestScreen, Is.EqualTo("screenA"));
        }
    }

    // -- all directions --

    [TestCase(Direction.Left, Direction.Right)]
    [TestCase(Direction.Right, Direction.Left)]
    [TestCase(Direction.Up, Direction.Down)]
    [TestCase(Direction.Down, Direction.Up)]
    public void Mirror_AllDirections(Direction source, Direction expected)
    {
        var hosts = new List<HostConfig>
        {
            Host("a", N(source, "b")),
            Host("b"),
        };

        HydraConfig.ExpandMirrors(hosts);

        Assert.That(hosts.First(h => h.Name == "b").Neighbours[0].Direction, Is.EqualTo(expected));
    }

    // -- missing target host auto-created --

    [Test]
    public void Mirror_MissingTargetHostCreated()
    {
        var hosts = new List<HostConfig>
        {
            Host("a", N(Direction.Right, "b")),
            // "b" not declared
        };

        HydraConfig.ExpandMirrors(hosts);

        var b = hosts.FirstOrDefault(h => h.Name == "b");
        Assert.That(b, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(b!.Neighbours, Has.Count.EqualTo(1));
            Assert.That(b.Neighbours[0].Direction, Is.EqualTo(Direction.Left));
        }
    }

    // -- generated mirror does not itself mirror (no infinite expansion) --

    [Test]
    public void Mirror_GeneratedEntryHasMirrorFalse()
    {
        var hosts = new List<HostConfig>
        {
            Host("a", N(Direction.Right, "b")),
            Host("b"),
        };

        HydraConfig.ExpandMirrors(hosts);

        var generated = hosts.First(h => h.Name == "b").Neighbours[0];
        Assert.That(generated.Mirror, Is.False);
    }
}
