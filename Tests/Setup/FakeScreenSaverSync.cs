using Hydra.Platform;

namespace Tests.Setup;

public sealed class FakeScreenSaverSync : IScreenSaverSync
{
    public bool LockScreenCalled;
    public TimeSpan? IdleTime;

    public event Action? ScreensaverActivated { add { } remove { } }
    public event Action? ScreensaverDeactivated { add { } remove { } }
    public event Action? ScreenLocked { add { } remove { } }
    public event Action? ScreenUnlocked { add { } remove { } }

    public void Activate() { }
    public void Deactivate() { }
    public void LockScreen() => LockScreenCalled = true;
    public void Suppress() { }
    public void Restore() { }
    public void ResetIdleTimer() { }
    public TimeSpan? GetIdleTime() => IdleTime;
}
