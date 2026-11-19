using Hydra.Platform;
using Hydra.Platform.Linux.Wayland;
using Hydra.Screen;

namespace Tests.Platform.Wayland;

// WaylandOutputs.Name: an InputCapture zone or EIS region takes the name of the output it lies on
[TestFixture]
public class WaylandOutputsTests
{
    private static DetectedScreen Output(int x, int y, int w, int h, string? name) => new(x, y, w, h, $"{name} monitor", name, name);

    [Test]
    public void SameBounds_TakeTheOutputsName()
    {
        var named = WaylandOutputs.Name(
            [new ScreenBounds(0, 0, 1920, 1080), new ScreenBounds(1920, 0, 2560, 1440)],
            [Output(1920, 0, 2560, 1440, "DP-2"), Output(0, 0, 1920, 1080, "DP-1")],
            "zone");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(named.Select(s => s.OutputName), Is.EqualTo(["DP-1", "DP-2"]));
            Assert.That(named.Select(s => s.DisplayName), Is.EqualTo(["DP-1 monitor", "DP-2 monitor"]));
            Assert.That(named[1].Bounds, Is.EqualTo(new ScreenBounds(1920, 0, 2560, 1440)));
        }
    }

    [Test]
    public void SameOrigin_OtherSize_StillMatches()
    {
        // a zone in physical pixels over an output in logical ones
        var named = WaylandOutputs.Name([new ScreenBounds(0, 0, 3840, 2160)], [Output(0, 0, 1920, 1080, "eDP-1")], "zone");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(named[0].OutputName, Is.EqualTo("eDP-1"));
            Assert.That(named[0].Bounds, Is.EqualTo(new ScreenBounds(0, 0, 3840, 2160)));
        }
    }

    [Test]
    public void NoMatch_KeepsTheIndexedName()
    {
        var named = WaylandOutputs.Name(
            [new ScreenBounds(0, 0, 1920, 1080), new ScreenBounds(5000, 0, 800, 600)],
            [Output(0, 0, 1920, 1080, "DP-1")],
            "region");
        Assert.That(named.Select(s => s.OutputName), Is.EqualTo(["DP-1", "region-1"]));
    }

    [Test]
    public void NoOutputs_AllIndexed()
    {
        var named = WaylandOutputs.Name([new ScreenBounds(0, 0, 10, 10), new ScreenBounds(10, 0, 10, 10)], [], "zone");
        Assert.That(named.Select(s => s.OutputName), Is.EqualTo(["zone-0", "zone-1"]));
    }

    [Test]
    public void UnnamedOutput_IsNeverMatched()
    {
        var named = WaylandOutputs.Name([new ScreenBounds(0, 0, 10, 10)], [Output(0, 0, 10, 10, null)], "zone");
        Assert.That(named[0].OutputName, Is.EqualTo("zone-0"));
    }

    [Test]
    public void AnOutput_NamesOneRegionOnly()
    {
        // an exact match is taken first, so the origin-only fallback cannot steal it
        var named = WaylandOutputs.Name(
            [new ScreenBounds(0, 0, 3840, 2160), new ScreenBounds(0, 0, 1920, 1080)],
            [Output(0, 0, 1920, 1080, "DP-1")],
            "zone");
        Assert.That(named.Select(s => s.OutputName), Is.EqualTo(["zone-0", "DP-1"]));
    }
}
