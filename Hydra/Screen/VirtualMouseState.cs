namespace Hydra.Screen;

public class VirtualMouseState
{
    public ScreenRect? CurrentScreen { get; private set; }
    public double X { get; private set; }
    public double Y { get; private set; }
    public bool IsOnVirtualScreen => CurrentScreen is not null;

    public void EnterScreen(ScreenRect screen, int x, int y)
    {
        CurrentScreen = screen;
        X = x;
        Y = y;
    }

    public void ApplyDelta(double dx, double dy)
    {
        if (CurrentScreen is null) return;
        X = Math.Clamp(X + dx, CurrentScreen.X, CurrentScreen.X + CurrentScreen.Width - 1);
        Y = Math.Clamp(Y + dy, CurrentScreen.Y, CurrentScreen.Y + CurrentScreen.Height - 1);
    }

    public void LeaveScreen()
    {
        CurrentScreen = null;
        X = 0;
        Y = 0;
    }
}
