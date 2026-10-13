using Hydra.Platform;

namespace Tests.Setup;

public sealed class FakeScreenSaverSync : IScreenSaverSync
{
    public bool LockScreenCalled;
    public bool ResetIdleTimerCalled;
    public int WakeDisplayCount;

    public event Action? ScreensaverActivated;
    public event Action? ScreensaverDeactivated;
    public event Action? ScreenLocked { add { } remove { } }
    public event Action? ScreenUnlocked { add { } remove { } }

    public void FireActivated() => ScreensaverActivated?.Invoke();
    public void FireDeactivated() => ScreensaverDeactivated?.Invoke();
    public void Activate() { }
    public void Deactivate() { }
    public void LockScreen() => LockScreenCalled = true;
    public void ResetIdleTimer() => ResetIdleTimerCalled = true;
    public void WakeDisplay() => WakeDisplayCount++;
}
