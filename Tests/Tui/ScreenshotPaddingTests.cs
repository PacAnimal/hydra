using System.IO.Compression;
using Tests.Setup;

namespace Tests.Tui;

/// <summary>
/// Every committed TUI screenshot is framed by exactly four pixels of terminal background, on all four
/// sides.
///
/// <para><b>This is a real defect the eye catches late.</b> The capture used to be cropped to an element's
/// box, which is subject to subpixel layout — and two columns of the render page's debug frame rode into a
/// committed PNG down its left edge and stayed there through several regenerations, while the top of the
/// window sat flush against the image and the bottom had a sliver of padding. The crop is now decided from
/// the PIXELS (<c>screenshot.mjs</c> trims to content and pads), and this is what stops it drifting back:
/// nobody re-examines a PNG's border by hand.</para>
///
/// <para>Asserted on the COMMITTED assets rather than by running the capture tool, which needs a Release
/// build, npm and a headless browser. What ships in the README is what is checked.</para>
/// </summary>
[TestFixture]
public class ScreenshotPaddingTests
{
    private const int ExpectedPadding = 4;

    /// <summary>
    /// xterm.js is themed <c>#0c0c0c</c>, so "black" here means the terminal's background — a screenshot
    /// has no pure-black border to measure against.
    /// </summary>
    /// <remarks>
    /// EXACT, with no tolerance. PNG is lossless, and the border these captures actually carry is one
    /// colour over every one of its pixels — so a tolerance would not absorb noise that exists, it would
    /// only widen what counts as background. At ±6 a debug frame painted <c>#101010</c> passes, which is
    /// precisely the class of defect this fixture was written for.
    /// </remarks>
    private static readonly (int R, int G, int B) Background = (12, 12, 12);

    /// <summary>
    /// What the capture tool emits, and only that. A wider glob would silently bind any PNG somebody drops
    /// in <c>docs/assets</c> — a logo, a diagram — to a rule about terminal backgrounds that has nothing to
    /// do with it.
    /// </summary>
    private static IEnumerable<string> Screenshots =>
        Directory.EnumerateFiles(Path.Combine(TestLog.SolutionRoot, "docs", "assets"), "hydra-tui-*.png").Order();

    [Test]
    public void ThereAreScreenshotsToCheck()
    {
        // The control. Without it, a rename of docs/assets turns every assertion below into a vacuous pass
        // over an empty sequence, and the padding rule quietly stops being enforced at all.
        Assert.That(Screenshots.Count(), Is.GreaterThan(0), "no screenshots found — has docs/assets moved?");
    }

    [TestCaseSource(nameof(Screenshots))]
    public void AScreenshotIsFramedByExactlyFourPixelsOfBackground(string path)
    {
        var image = Png.Decode(File.ReadAllBytes(path));

        var top = FirstRowWithContent(image, Enumerable.Range(0, image.Height));
        var bottom = image.Height - 1 - FirstRowWithContent(image, Enumerable.Range(0, image.Height).Reverse());
        var left = FirstColumnWithContent(image, Enumerable.Range(0, image.Width));
        var right = image.Width - 1 - FirstColumnWithContent(image, Enumerable.Range(0, image.Width).Reverse());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(top, Is.EqualTo(ExpectedPadding), $"{Path.GetFileName(path)}: padding above the content");
            Assert.That(bottom, Is.EqualTo(ExpectedPadding), $"{Path.GetFileName(path)}: padding below the content");
            Assert.That(left, Is.EqualTo(ExpectedPadding), $"{Path.GetFileName(path)}: padding left of the content");
            Assert.That(right, Is.EqualTo(ExpectedPadding), $"{Path.GetFileName(path)}: padding right of the content");
        }
    }

    /// <summary>
    /// The content STARTS at the fifth pixel in, on all four sides — at least one non-background pixel
    /// sitting exactly on that line.
    ///
    /// <para>Implied by <see cref="AScreenshotIsFramedByExactlyFourPixelsOfBackground"/>, which asserts the
    /// first row WITH content is row 4 and therefore covers both directions already. Kept because it says
    /// which EDGE drifted rather than which measurement did, and because it states the property in the form
    /// somebody reads it in. A whole line of content is not required — the top edge is six pixels of window
    /// corner — but one pixel must be there.</para>
    /// </summary>
    [TestCaseSource(nameof(Screenshots))]
    public void ContentTouchesTheFifthPixelOnEverySide(string path)
    {
        var image = Png.Decode(File.ReadAllBytes(path));
        var name = Path.GetFileName(path);

        var columns = Enumerable.Range(0, image.Width).ToArray();
        var rows = Enumerable.Range(0, image.Height).ToArray();

        var top = columns.Count(x => !IsBackground(image, x, ExpectedPadding));
        var bottom = columns.Count(x => !IsBackground(image, x, image.Height - 1 - ExpectedPadding));
        var left = rows.Count(y => !IsBackground(image, ExpectedPadding, y));
        var right = rows.Count(y => !IsBackground(image, image.Width - 1 - ExpectedPadding, y));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(top, Is.GreaterThan(0), $"{name}: row {ExpectedPadding} is all background — the padding above the content is too wide");
            Assert.That(bottom, Is.GreaterThan(0), $"{name}: row {image.Height - 1 - ExpectedPadding} is all background — the padding below the content is too wide");
            Assert.That(left, Is.GreaterThan(0), $"{name}: column {ExpectedPadding} is all background — the padding left of the content is too wide");
            Assert.That(right, Is.GreaterThan(0), $"{name}: column {image.Width - 1 - ExpectedPadding} is all background — the padding right of the content is too wide");
        }
    }

    /// <summary>
    /// The border is background and nothing else — a frame of the right WIDTH could still be the wrong
    /// colour, which is exactly how the old debug frame survived: it was a perfectly even two pixels.
    /// </summary>
    [TestCaseSource(nameof(Screenshots))]
    public void AScreenshotsBorderIsNothingButBackground(string path)
    {
        var image = Png.Decode(File.ReadAllBytes(path));

        for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
            {
                var onBorder = y < ExpectedPadding || y >= image.Height - ExpectedPadding
                               || x < ExpectedPadding || x >= image.Width - ExpectedPadding;
                if (!onBorder) continue;

                if (!IsBackground(image, x, y))
                    Assert.Fail($"{Path.GetFileName(path)}: ({x},{y}) is in the border and is {image.At(x, y)}, not the terminal background");
            }
    }

    private static bool IsBackground(Png.Image image, int x, int y)
    {
        var (r, g, b) = image.At(x, y);
        return (r, g, b) == Background;
    }

    private static int FirstRowWithContent(Png.Image image, IEnumerable<int> rows) =>
        rows.First(y => Enumerable.Range(0, image.Width).Any(x => !IsBackground(image, x, y)));

    private static int FirstColumnWithContent(Png.Image image, IEnumerable<int> columns) =>
        columns.First(x => Enumerable.Range(0, image.Height).Any(y => !IsBackground(image, x, y)));

    /// <summary>
    /// Just enough PNG to read a pixel back.
    ///
    /// <para>Hand-rolled because the alternative is an imaging package carried by the whole test project for
    /// one border check — <c>System.Drawing</c> is Windows-only, and this suite runs on three platforms.
    /// Truecolour only, 8 bits per channel, which is what the capture tool emits; anything else is refused
    /// loudly rather than silently mis-read.</para>
    /// </summary>
    private static class Png
    {
        internal sealed record Image(int Width, int Height, int Channels, byte[] Pixels)
        {
            internal (int R, int G, int B) At(int x, int y)
            {
                var offset = (y * Width + x) * Channels;
                return (Pixels[offset], Pixels[offset + 1], Pixels[offset + 2]);
            }
        }

        internal static Image Decode(byte[] file)
        {
            var width = 0;
            var height = 0;
            var colourType = -1;
            using var idat = new MemoryStream();

            var pos = 8; // the signature
            while (pos + 8 <= file.Length)
            {
                var length = ReadBigEndian(file, pos);
                var type = System.Text.Encoding.ASCII.GetString(file, pos + 4, 4);
                var data = pos + 8;

                switch (type)
                {
                    case "IHDR":
                        width = ReadBigEndian(file, data);
                        height = ReadBigEndian(file, data + 4);
                        var bitDepth = file[data + 8];
                        colourType = file[data + 9];
                        Assert.That(bitDepth, Is.EqualTo(8), "only 8-bit channels are read here");
                        Assert.That(colourType, Is.AnyOf(2, 6), "only truecolour PNGs are read here (2 = RGB, 6 = RGBA)");
                        Assert.That(file[data + 12], Is.Zero, "interlaced PNGs are not read here");
                        break;
                    case "IDAT":
                        idat.Write(file, data, length);
                        break;
                }

                pos = data + length + 4; // + CRC
            }

            var channels = colourType == 6 ? 4 : 3;
            idat.Position = 0;
            using var inflate = new ZLibStream(idat, CompressionMode.Decompress);
            using var raw = new MemoryStream();
            inflate.CopyTo(raw);

            // ONE LINE THAT CLOSES EVERYTHING THIS DECODER DOES NOT IMPLEMENT. An interlaced PNG inflates to
            // a LONGER stream than this layout, a truncated one to a shorter — and neither would throw
            // below, they would unfilter into plausible noise and be measured as if they were the image.
            // Checking the length refuses both, along with a missing IHDR and a channel count we guessed
            // wrong, without this fixture having to learn Adam7.
            Assert.That(raw.Length, Is.EqualTo((long)(width * channels + 1) * height),
                "not a non-interlaced 8-bit truecolour PNG — this decoder would read it as noise rather than fail");

            return new Image(width, height, channels, Unfilter(raw.ToArray(), width, height, channels));
        }

        /// <summary>
        /// Reverses the per-scanline filters. Every PNG in the wild uses these, and a decoder that skipped
        /// them would read plausible-looking noise rather than fail.
        /// </summary>
        private static byte[] Unfilter(byte[] raw, int width, int height, int channels)
        {
            var stride = width * channels;
            var pixels = new byte[stride * height];
            var previous = new byte[stride];
            var read = 0;

            for (var y = 0; y < height; y++)
            {
                var filter = raw[read++];
                var line = new byte[stride];
                Array.Copy(raw, read, line, 0, stride);
                read += stride;

                for (var x = 0; x < stride; x++)
                {
                    int left = x >= channels ? line[x - channels] : 0;
                    int up = previous[x];
                    int upLeft = x >= channels ? previous[x - channels] : 0;

                    line[x] = filter switch
                    {
                        0 => line[x],
                        1 => (byte)(line[x] + left),
                        2 => (byte)(line[x] + up),
                        3 => (byte)(line[x] + (left + up) / 2),
                        4 => (byte)(line[x] + Paeth(left, up, upLeft)),
                        _ => throw new InvalidDataException($"unknown PNG scanline filter {filter}")
                    };
                }

                Array.Copy(line, 0, pixels, y * stride, stride);
                previous = line;
            }

            return pixels;
        }

        private static int Paeth(int a, int b, int c)
        {
            var p = a + b - c;
            int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }

        private static int ReadBigEndian(byte[] bytes, int offset) =>
            (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];
    }
}
