using Cathedral.Utils;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform;

// shared polling loop for screensaver detection.
// subclasses implement IsScreensaverOn() and the activation/suppression methods.
// always running as a hosted service; fires ScreensaverActivated/Deactivated events on state changes.
public abstract class PollingScreenSaverSync(ILogger log) : SimpleHostedService(log, TimeSpan.FromSeconds(1)), IScreenSaverSync
{
    private readonly ILogger _log = log;
    private bool _wasOn;

    public event Action? ScreensaverActivated;
    public event Action? ScreensaverDeactivated;
    public event Action? ScreenLocked;
    public event Action? ScreenUnlocked;

    protected abstract bool IsScreensaverOn();
    public abstract void Activate();
    public abstract void Deactivate();
    public abstract void Suppress();
    public abstract void Restore();
    public virtual void LockScreen() { }
    public virtual void ResetIdleTimer() => Suppress();
    public virtual TimeSpan? GetIdleTime() => null;

    protected void OnScreenLocked() => ScreenLocked?.Invoke();
    protected void OnScreenUnlocked() => ScreenUnlocked?.Invoke();

    protected override Task Execute(CancellationToken cancel)
    {
        var isOn = IsScreensaverOn();
        if (isOn && !_wasOn)
        {
            _log.LogInformation("Screensaver started (poll detected)");
            ScreensaverActivated?.Invoke();
        }
        else if (!isOn && _wasOn)
        {
            _log.LogInformation("Screensaver stopped (poll detected)");
            ScreensaverDeactivated?.Invoke();
        }
        _wasOn = isOn;
        return Task.CompletedTask;
    }
}
