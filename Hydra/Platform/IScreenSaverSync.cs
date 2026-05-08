namespace Hydra.Platform;

public interface IScreenSaverSync
{
    // master-side: watch for local screensaver activation/deactivation
    void StartWatching(Action onActivated, Action onDeactivated);
    void StopWatching();

    // slave-side: activate/deactivate local screensaver on command
    void Activate();
    void Deactivate();

    // slave-side: suppress/restore idle timer (called periodically by ScreensaverSuppressor)
    void Suppress();
    void Restore();

    // master-side: one-shot reset of the local idle timer (called on input activity while cursor is on a slave)
    void ResetIdleTimer();
}
