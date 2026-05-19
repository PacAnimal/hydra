namespace Hydra.Platform;

public class NullScreenSaverSync : IScreenSaverSync
{
    public event Action? ScreensaverActivated { add { } remove { } }
    public event Action? ScreensaverDeactivated { add { } remove { } }
    public event Action? ScreenLocked { add { } remove { } }

    public void Activate() { }
    public void Deactivate() { }
    public void LockScreen() { }
    public void Suppress() { }
    public void Restore() { }
    public void ResetIdleTimer() { }
}
