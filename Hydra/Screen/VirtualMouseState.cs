namespace Hydra.Screen;

public class VirtualMouseState
{
    public ScreenRect? CurrentScreen { get; private set; }
    public List<ScreenRect> RemoteScreens { get; private set; } = [];
    public double X { get; private set; }   // position within CurrentScreen (screen-local)
    public double Y { get; private set; }
    public decimal Scale { get; private set; } = 1.0m;
    public bool IsOnVirtualScreen => CurrentScreen is not null;

    // per-screen scales from ScreenDefinitions; populated from ScreenInfo at EnterScreen
    private Dictionary<string, decimal> _scaleMap = new(StringComparer.OrdinalIgnoreCase);

    public void EnterScreen(ScreenRect screen, List<ScreenRect> allScreens, int x, int y, decimal scale = 1.0m, Dictionary<string, decimal>? scaleMap = null)
    {
        CurrentScreen = screen;
        RemoteScreens = allScreens.Count > 0 ? allScreens : [screen];
        X = x;
        Y = y;
        Scale = scale;
        _scaleMap = scaleMap ?? new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
    }

    // returns the previous screen if a screen transition occurred, null otherwise
    public ScreenRect? ApplyDelta(double dx, double dy)
    {
        if (CurrentScreen is null) return null;

        var prev = CurrentScreen;
        var candidateX = X + dx * (double)Scale;
        var candidateY = Y + dy * (double)Scale;

        // convert to host-global coords
        var globalX = CurrentScreen.X + candidateX;
        var globalY = CurrentScreen.Y + candidateY;

        // still within current screen?
        if (CurrentScreen.Contains(globalX, globalY))
        {
            X = candidateX;
            Y = candidateY;
            return null;
        }

        // check if another remote screen contains this global position
        foreach (var screen in RemoteScreens)
        {
            if (screen == CurrentScreen) continue;
            if (screen.Contains(globalX, globalY))
            {
                CurrentScreen = screen;
                X = globalX - screen.X;
                Y = globalY - screen.Y;
                // update scale for the new screen
                if (_scaleMap.TryGetValue(screen.Name, out var newScale))
                    Scale = newScale;
                return prev;
            }
        }

        // dead zone — clamp to nearest valid screen position
        ClampToNearest(globalX, globalY);
        return CurrentScreen != prev ? prev : null;
    }

    public void LeaveScreen()
    {
        CurrentScreen = null;
        RemoteScreens = [];
        X = 0;
        Y = 0;
        Scale = 1.0m;
        _scaleMap.Clear();
    }

    private void ClampToNearest(double globalX, double globalY)
    {
        // find the screen with the smallest clamped distance and snap to it
        ScreenRect? best = CurrentScreen ?? (RemoteScreens.Count > 0 ? RemoteScreens[0] : null);
        if (best is null) return;

        var bestDistSq = double.MaxValue;
        foreach (var screen in RemoteScreens)
        {
            var cx = Math.Clamp(globalX, screen.X, screen.X + screen.Width - 1);
            var cy = Math.Clamp(globalY, screen.Y, screen.Y + screen.Height - 1);
            var dx = globalX - cx;
            var dy = globalY - cy;
            var distSq = dx * dx + dy * dy;
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                best = screen;
            }
        }

        CurrentScreen = best;
        X = Math.Clamp(globalX - best.X, 0, best.Width - 1);
        Y = Math.Clamp(globalY - best.Y, 0, best.Height - 1);
        if (_scaleMap.TryGetValue(best.Name, out var newScale))
            Scale = newScale;
    }
}
