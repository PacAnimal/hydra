using Hydra.Screen;

namespace Hydra.Platform.Linux;

// where the X11 OSD goes and what of it is solid, apart from Xlib so it can be tested anywhere
internal static class OsdLayout
{
    // the screen under the pointer, else the one nearest to it; null when there is none
    internal static ScreenBounds? ScreenAt(IReadOnlyList<ScreenBounds> screens, ScreenPoint pointer) =>
        screens.FirstOrDefault(s => pointer.X >= s.X && pointer.X < s.X + s.Width && pointer.Y >= s.Y && pointer.Y < s.Y + s.Height)
        ?? screens.MinBy(s => DistanceSquared(s, pointer));

    // the rows of an ARGB32 image (little-endian, alpha the 4th byte) as runs of pixels at least half opaque: the
    // window's shape where no compositor blends its alpha, so the text shows without a black box round it
    internal static List<XRectangle> OpaqueRuns(byte[] pixels, int width, int height, int stride)
    {
        var runs = new List<XRectangle>();
        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            var x = 0;
            while (x < width)
            {
                while (x < width && pixels[row + x * 4 + 3] < 0x80) x++;
                var start = x;
                while (x < width && pixels[row + x * 4 + 3] >= 0x80) x++;
                if (x > start) runs.Add(new XRectangle { X = (short)start, Y = (short)y, Width = (ushort)(x - start), Height = 1 });
            }
        }
        return runs;
    }

    private static long DistanceSquared(ScreenBounds s, ScreenPoint p)
    {
        long dx = p.X < s.X ? s.X - p.X : p.X >= s.X + s.Width ? p.X - (s.X + s.Width - 1) : 0;
        long dy = p.Y < s.Y ? s.Y - p.Y : p.Y >= s.Y + s.Height ? p.Y - (s.Y + s.Height - 1) : 0;
        return dx * dx + dy * dy;
    }
}
