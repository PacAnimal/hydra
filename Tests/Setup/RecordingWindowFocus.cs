using Hydra.Platform;
using Hydra.Screen;

namespace Tests.Setup;

// records each screen a window was to be focused on
public sealed class RecordingWindowFocus : IWindowFocus
{
    public List<ScreenBounds> Focused { get; } = [];

    public void FocusFrontmostOn(ScreenBounds screen) => Focused.Add(screen);
}
