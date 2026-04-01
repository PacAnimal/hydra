namespace Hydra.Screen;

public class VirtualMouseState
{
    public ScreenRect? CurrentScreen { get; private set; }
    public double X { get; private set; }
    public double Y { get; private set; }
    public decimal Scale { get; private set; } = 1.0m;
    public bool IsOnVirtualScreen => CurrentScreen is not null;

    public void EnterScreen(ScreenRect screen, int x, int y, decimal scale = 1.0m)
    {
        CurrentScreen = screen;
        X = x;
        Y = y;
        Scale = scale;
    }

    public void ApplyDelta(double dx, double dy)
    {
        if (CurrentScreen is null) return;
        X = Math.Clamp(X + dx * (double)Scale, 0, CurrentScreen.Width - 1);
        Y = Math.Clamp(Y + dy * (double)Scale, 0, CurrentScreen.Height - 1);
    }

    public void LeaveScreen()
    {
        CurrentScreen = null;
        X = 0;
        Y = 0;
        Scale = 1.0m;
    }
}
