namespace Hydra.Platform;

public interface IScreenSaverSync
{
    // master-side: watch for local screensaver activation/deactivation
    void StartWatching(Action onActivated, Action onDeactivated);
    void StopWatching();

    // master-side: watch for local machine lock (Mac and Windows only; no-op elsewhere)
    void StartWatchingLock(Action onLocked);

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
