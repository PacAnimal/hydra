using Cathedral.Extensions;
using Hydra.Config;
using Hydra.Platform;
using Microsoft.Extensions.Logging;

namespace Hydra.Relay;

public interface IActivityTracker
{
    // ms elapsed since last LocalActivity() call; used to guard against spurious lock-screen
    long MsSinceLocalActivity { get; }
    ValueTask LocalActivity();
    ValueTask RemoteActivity(string sourcePeer);
    // poke the local idle timer on an incoming ping; does not update MsSinceLocalActivity
    ValueTask IncomingPing();
}

// tracks activity on one machine and propagates it to peers, throttled to once per 5 seconds.
// master: only propagates when syncScreensaver is on.
// slave: always propagates (master decides whether to act on it).
public sealed class ActivityTracker(IHydraProfile profile, Lazy<IRelaySender> relay, IWorldState worldState,
    IScreenSaverSync screenSaverSync, ILogger<ActivityTracker> log, Func<long>? getClock = null) : IActivityTracker
{
    private readonly Func<long> _getClock = getClock ?? (() => Environment.TickCount64);
    private long _lastSentTick;
    private long _lastLocalActivityTick;

    public long MsSinceLocalActivity => _getClock() - Interlocked.Read(ref _lastLocalActivityTick);

    // call on any local input (master: keyboard/mouse; slave: keyboard/mouse/cursor movement).
    // called on ALL input, not only while the cursor is on a slave — this keeps screensavers in sync
    // across the whole KVM session, not just when you're actively controlling a remote machine.
    // pokes the local idle timer and broadcasts to peers, both throttled to once per 5 seconds.
    public ValueTask LocalActivity()
    {
        Interlocked.Exchange(ref _lastLocalActivityTick, _getClock());
        return BroadcastIfDue();
    }

    // call on slave when an activity ping arrives from master.
    // resets the local idle timer without updating MsSinceLocalActivity — only real local input
    // should update that, so the lock-screen guard isn't fooled by incoming pings.
    public ValueTask IncomingPing()
    {
        screenSaverSync.ResetIdleTimer();
        return ValueTask.CompletedTask;
    }

    // call on master when an activity ping arrives from a slave.
    // also resets the master's own idle timer — if any machine in the session is active,
    // none of the screensavers should kick in.
    public async ValueTask RemoteActivity(string sourcePeer)
    {
        if (!profile.SyncScreensaver) return;
        await BroadcastIfDue(sourcePeer);
    }

    private async ValueTask BroadcastIfDue(string? excludePeer = null)
    {
        var now = _getClock();
        var last = Interlocked.Read(ref _lastSentTick);
        if (now - last < 5000) return;
        if (Interlocked.CompareExchange(ref _lastSentTick, now, last) != last) return;
        log.LogDebug("Resetting local idle timer");
        screenSaverSync.ResetIdleTimer();
        // slave always broadcasts (master decides whether to act); master only broadcasts when syncScreensaver is on
        if (profile.Mode != Mode.Slave && !profile.SyncScreensaver) return;
        var peers = profile.Mode == Mode.Slave
            ? await worldState.GetMasters()
            : [.. (await worldState.GetPeerScreensSnapshot()).Keys];
        var targets = excludePeer == null
            ? peers
            : [.. peers.Where(p => !p.EqualsIgnoreCase(excludePeer))];
        if (targets.Length == 0) return;
        log.LogDebug("Activity ping → {Targets}", string.Join(", ", targets));
        relay.Value.Send(targets, MessageSerializer.Encode(MessageKind.ActivityPing, new ActivityPingMessage()));
    }
}
