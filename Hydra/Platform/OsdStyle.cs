using Hydra.Screen;

namespace Hydra.Platform;

// the OSD's look and timing, shared by the Windows and X11 OSDs, which both draw their own text; macOS's shield draws
// its own in Swift (Resources/MacShield/main.swift, OsdPanel) to the same figures: 28 pt bold, shown 1.5 s, faded 0.3 s,
// centred 10% of the screen's height above its bottom
internal static class OsdStyle
{
    internal static readonly TimeSpan ShowFor = TimeSpan.FromSeconds(1.5);
    internal static readonly TimeSpan FadeFor = TimeSpan.FromSeconds(0.3);
    internal static readonly TimeSpan FadeTick = TimeSpan.FromMilliseconds(16); // ~60 fps

    // the text's em size at 96 dpi (100% scaling), as the macOS OSD's 28 pt
    internal const double EmSize = 28;
    // the clear margin round the text, a fraction of the em size
    internal const double Padding = 0.6;

    // a graduated dark glow standing in for a blurred shadow: each pass's line width as a fraction of the em size, and
    // its alpha, outermost (widest, faintest) first
    internal static readonly (double WidthFactor, byte Alpha)[] GlowPasses = [(0.20, 40), (0.13, 80), (0.08, 140), (0.05, 200)];
    // the near-white fill, then a hard 1 px dark outline over it
    internal const byte FillAlpha = 240, FillGrey = 242, OutlineAlpha = 200;

    // the image's margin in pixels for an em size
    internal static int PaddingFor(double emSize) => (int)(emSize * Padding);

    // centred across the screen, its bottom 10% of the screen's height above the screen's bottom
    internal static ScreenPoint Place(ScreenBounds screen, int width, int height) =>
        new(screen.X + (screen.Width - width) / 2, screen.Y + screen.Height - height - (int)(screen.Height * 0.1));
}
