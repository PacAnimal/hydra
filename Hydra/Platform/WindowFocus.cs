using Hydra.Screen;

namespace Hydra.Platform;

// Brings forward the window a user last worked in on a screen, where the monitor switch hotkey lands: the master for its
// own screens, a slave for the one its master names.
public interface IWindowFocus
{
    // focuses the frontmost ordinary window whose centre lies on screen (in host coordinates), if there is one
    void FocusFrontmostOn(ScreenBounds screen);
}

public sealed class NullWindowFocus : IWindowFocus
{
    public void FocusFrontmostOn(ScreenBounds screen) { }
}

// a top-level window as a platform lists it: its id, the process owning it (where raising it needs that), and its bounds
internal sealed record ListedWindow(nint Id, int Pid, ScreenBounds Bounds);

internal static class WindowPicker
{
    // A window system stacks windows in the order they were last active, so the frontmost one on a screen is the one last
    // worked in there; a window is on the screen holding its centre.
    internal static ListedWindow? FrontmostOn(IEnumerable<ListedWindow> frontToBack, ScreenBounds screen) =>
        frontToBack.FirstOrDefault(w => w.Bounds is { Width: > 0, Height: > 0 } b && Holds(screen, b.X + b.Width / 2.0, b.Y + b.Height / 2.0));

    private static bool Holds(ScreenBounds screen, double x, double y) =>
        x >= screen.X && x < screen.X + screen.Width && y >= screen.Y && y < screen.Y + screen.Height;
}
