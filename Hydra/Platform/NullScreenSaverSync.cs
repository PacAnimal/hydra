namespace Hydra.Platform;

public class NullScreenSaverSync : IScreenSaverSync
{
    public void StartWatching(Action onActivated, Action onDeactivated) { }
    public void StopWatching() { }
    public void StartWatchingLock(Action onLocked) { }
    public void Activate() { }
    public void Deactivate() { }
    public void LockScreen() { }
    public void Suppress() { }
    public void Restore() { }
    public void ResetIdleTimer() { }
}
