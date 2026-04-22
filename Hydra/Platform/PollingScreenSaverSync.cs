using Microsoft.Extensions.Logging;

namespace Hydra.Platform;

// shared polling loop for screensaver detection.
// subclasses implement IsScreensaverOn() and the activation/suppression methods.
public abstract class PollingScreenSaverSync(ILogger? log = null) : IScreenSaverSync
{
    private CancellationTokenSource? _watchCts;

    public virtual void StartWatching(Action onActivated, Action onDeactivated)
    {
        log?.LogInformation("Watching for screensaver state changes (polling)");
        _watchCts = new CancellationTokenSource();
        var ct = _watchCts.Token;
        _ = Task.Run(async () => await PollAsync(onActivated, onDeactivated, ct), ct);
    }

    public void StopWatching()
    {
        log?.LogInformation("Stopped watching for screensaver state changes");
        _watchCts?.Cancel();
        _watchCts?.Dispose();
        _watchCts = null;
    }

    protected abstract bool IsScreensaverOn();
    public abstract void Activate();
    public abstract void Deactivate();
    public abstract void Suppress();
    public abstract void Restore();

    private async Task PollAsync(Action onActivated, Action onDeactivated, CancellationToken ct)
    {
        var wasOn = false;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var isOn = IsScreensaverOn();
                if (isOn && !wasOn)
                {
                    log?.LogInformation("Screensaver started (poll detected)");
                    onActivated();
                }
                else if (!isOn && wasOn)
                {
                    log?.LogInformation("Screensaver stopped (poll detected)");
                    onDeactivated();
                }
                wasOn = isOn;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { log?.LogWarning(ex, "Screensaver poll error"); }
    }
}
