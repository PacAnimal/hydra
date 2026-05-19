namespace Hydra.Platform;

public interface IScreenSaverSync
{
    // master-side: fired when screensaver activates/deactivates on this machine
    event Action? ScreensaverActivated;
    event Action? ScreensaverDeactivated;

    // master-side: fired when this machine's screen is locked (Mac and Windows only)
    event Action? ScreenLocked;

    // slave-side: activate/deactivate local screensaver on command
    void Activate();
    void Deactivate();

    // slave-side: lock the local machine (Mac: ctrl+cmd+q, Windows: LockWorkStation; no-op elsewhere)
    void LockScreen();

    // slave-side: suppress/restore idle timer (called periodically by ScreensaverSuppressor)
    void Suppress();
    void Restore();

    // master-side: one-shot reset of the local idle timer (called on input activity while cursor is on a slave)
    void ResetIdleTimer();
}
