using Hydra.Config;
using Hydra.Relay;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform;

// One platform-neutral sleep boundary. Platform callbacks are deliberately thin: they ask this
// coordinator to quiesce the relay, acknowledge the OS notification, and resume after hardware is ready.
// Native lifecycle sources deliver an ordered event stream but do not provide a sleep-cycle token;
// this coordinator assigns the generation that lets the relay reject delayed internal work from older cycles.
internal sealed class SystemSleepCoordinator(
    IHydraProfile profile,
    IRelaySender relay,
    ILogger<SystemSleepCoordinator> log)
{
    private readonly Lock _stateLock = new();
    private int _sleepRequested;
    private bool _wakeStarted;
    private bool _wakeCompleted;
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
            _wakeStarted = false;
            _wakeCompleted = false;
            generation = ++_sleepGeneration;
        }

        log.LogInformation("System sleep requested — closing relay connection");
        try
        {
            await relay.SuspendForSystemSleepAsync(generation, cancel);
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

        // Wake notifications can race a slow relay teardown. Reapply the newest wake phase after
        // teardown so an early or completed wake cannot be lost behind suspension.
        bool wakeStarted;
        bool wakeCompleted;
        lock (_stateLock)
        {
            var sameCycle = _sleepGeneration == generation;
            wakeStarted = sameCycle && _wakeStarted;
            wakeCompleted = sameCycle && _wakeCompleted;
        }
        if (wakeCompleted)
        {
            log.LogInformation("System finished resuming during relay shutdown — reconnecting relay");
            relay.CompleteSystemWake(generation);
        }
        else if (wakeStarted)
        {
            log.LogInformation("System began resuming during relay shutdown — reconnecting relay");
            relay.BeginSystemWake(generation);
        }
    }

    internal void BeginResumeAfterSleep()
    {
        if (!Enabled) return;
        long generation;
        lock (_stateLock)
        {
            if (_sleepRequested == 0 || _wakeStarted) return;
            _wakeStarted = true;
            generation = _sleepGeneration;
        }
        log.LogInformation("System wake started — reconnecting relay as resources become available");
        relay.BeginSystemWake(generation);
    }

    internal void ResumeAfterSleep()
    {
        if (!Enabled) return;
        long generation;
        lock (_stateLock)
        {
            if (_sleepRequested == 0) return;
            _sleepRequested = 0;
            _wakeStarted = true;
            _wakeCompleted = true;
            generation = _sleepGeneration;
        }
        log.LogInformation("System wake completed — finalizing relay reconnection");
        relay.CompleteSystemWake(generation);
    }
}
