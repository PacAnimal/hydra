using Hydra.Config;
using Hydra.Relay;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform;

// One platform-neutral sleep boundary. Platform callbacks are deliberately thin: they ask this
// coordinator to quiesce the relay, acknowledge the OS notification, and resume after hardware is ready.
internal sealed class SystemSleepCoordinator(
    IHydraProfile profile,
    IRelaySender relay,
    ILogger<SystemSleepCoordinator> log)
{
    private readonly Lock _stateLock = new();
    private int _sleepRequested;
    private long _sleepGeneration;

    internal bool Enabled => profile.AllowSystemSleep;

    internal async Task PrepareForSleepAsync(CancellationToken cancel)
    {
        if (!Enabled) return;

        long generation;
        lock (_stateLock)
        {
            if (_sleepRequested != 0) return;
            _sleepRequested = 1;
            generation = ++_sleepGeneration;
        }

        log.LogInformation("System sleep requested — closing relay connection");
        try
        {
            await relay.SuspendConnectionAsync(cancel);
            log.LogInformation("Relay connection closed for system sleep");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            log.LogWarning("Timed out waiting for relay connection to close before system sleep");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to close relay connection before system sleep");
        }

        // A wake notification can race a slow relay teardown. If that wake belongs to this same
        // sleep cycle, resume once more after teardown so the earlier wake cannot be lost.
        bool wakeWonRace;
        lock (_stateLock)
            wakeWonRace = _sleepRequested == 0 && _sleepGeneration != generation;
        if (wakeWonRace)
        {
            log.LogInformation("System resumed during relay shutdown — reconnecting relay");
            relay.ResumeConnection();
        }
    }

    internal void ResumeAfterSleep()
    {
        if (!Enabled) return;
        lock (_stateLock)
        {
            if (_sleepRequested == 0) return;
            _sleepRequested = 0;
            _sleepGeneration++;
        }
        log.LogInformation("System resumed — reconnecting relay");
        relay.ResumeConnection();
    }
}
