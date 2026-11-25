using Hydra.Platform.Linux;
using Hydra.Screen;

namespace Tests.Platform;

// which screen the X11 OSD goes on, and which of its pixels make its shape where nothing composites
[TestFixture]
public class OsdLayoutTests
{
    private static readonly ScreenBounds Left = new(0, 0, 2560, 1440);
    private static readonly ScreenBounds Right = new(2560, 0, 1920, 1080);

    [Test]
    public void TheScreenUnderThePointer_IsChosen()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(OsdLayout.ScreenAt([Left, Right], new ScreenPoint(100, 100)), Is.EqualTo(Left));
            Assert.That(OsdLayout.ScreenAt([Left, Right], new ScreenPoint(2560, 0)), Is.EqualTo(Right));
            Assert.That(OsdLayout.ScreenAt([Left, Right], new ScreenPoint(2559, 1439)), Is.EqualTo(Left));
        }
    }

    // below the shorter right screen is on no screen; the nearest one is the right one, straight above
    [Test]
    public void APointerOnNoScreen_TakesTheNearest()
    {
        Assert.That(OsdLayout.ScreenAt([Left, Right], new ScreenPoint(3000, 1300)), Is.EqualTo(Right));
    }

    [Test]
    public void NoScreens_IsNoScreen()
    {
        Assert.That(OsdLayout.ScreenAt([], new ScreenPoint(0, 0)), Is.Null);
    }

    // ARGB32 rows: alpha is each pixel's 4th byte; any alpha is ink; a row may have padding past its pixels
    [Test]
    public void InkRuns_AreTheInkedSpansOfEachRow()
    {
        const int width = 5, height = 2, stride = 24;
        var pixels = new byte[stride * height];
        void Alpha(int x, int y, byte a) => pixels[y * stride + x * 4 + 3] = a;
        Alpha(1, 0, 0xFF);
        Alpha(2, 0, 0x01);
        Alpha(4, 0, 0x28);
        Alpha(0, 1, 0xC0);
        pixels[1 * stride + 5 * 4 + 3] = 0xFF; // padding past the row's last pixel

        var runs = OsdLayout.InkRuns(pixels, width, height, stride);

        Assert.That(runs.Select(r => (r.X, r.Y, r.Width, r.Height)), Is.EqualTo(new (short, short, ushort, ushort)[]
        {
            (1, 0, 2, 1),
            (4, 0, 1, 1),
            (0, 1, 1, 1),
        }));
    }
}
