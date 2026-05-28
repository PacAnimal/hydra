namespace Hydra.Platform;

public interface IScreenSaverSync
{
    // master-side: fired when screensaver activates/deactivates on this machine
    event Action? ScreensaverActivated;
    event Action? ScreensaverDeactivated;

    // master-side: fired when this machine's screen is locked/unlocked (Mac and Windows only)
    event Action? ScreenLocked;
    event Action? ScreenUnlocked;

    // slave-side: activate/deactivate local screensaver on command
    void Activate();
    void Deactivate();

    // slave-side: lock the local machine (Mac: ctrl+cmd+q, Windows: LockWorkStation; no-op elsewhere)
    void LockScreen();

    // poke the local idle timer — called on activity to prevent screensaver from activating
    void ResetIdleTimer();
}
