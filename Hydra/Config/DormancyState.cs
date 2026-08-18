using Cathedral.Utils;
using Microsoft.Extensions.Logging;

namespace Hydra.Config;

// Dormant is the state between "fully running" and "idle": the active profile's conditions stopped
// matching for a reason a wake can undo — the displays slept, or macOS started reporting battery while
// the machine idles on the dock. Restarting into idle there drops us off the relay entirely, and a
// master's cursor parked on this machine would be flung back home for what is really just a dark screen.
// So we stay connected and refuse input instead. The master needs to know none of this: it keeps sending
// input at us as though nothing happened, and that input is itself the wake.
//
// The first refused input starts a deadline. Either we get our screens and power back and match the
// profile again — the cursor still sitting exactly where its owner left it — or we leave the relay, which
// is what finally tells the master to reclaim its cursor. That decision is ours, never the master's.
//
// Deliberately in-memory only: any restart forgets it and comes up genuinely idle, so a machine that was
// carried off and rebooted stays asleep until its owner opens the lid.
public interface IDormancyState
{
    bool IsDormant { get; }
    event Func<Task>? Entered;
    event Func<Task>? Exited;

    // raised when the deadline lapsed and the profile still doesn't match — time to leave the relay
    event Func<Task>? WakeDeadlineExpired;

    Task Enter();
    Task Exit();

    // arms the wake deadline. Returns true only the first time in a dormant episode, so a stream of input
    // can't keep pushing the deadline out of reach.
    bool RequestWake();
}

public sealed class DormancyState(ILogger<DormancyState> log, Func<DateTime>? clock = null) : SimpleHostedService(log, TimeSpan.FromSeconds(1)), IDormancyState
{
    // how long a woken machine has to bring its displays and power back and match the profile again
    public static readonly TimeSpan WakeDeadline = TimeSpan.FromSeconds(30);

    private readonly ILogger<DormancyState> _log = log;
    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);
    private readonly Toggle _dormant = new();
    private readonly Lock _wakeLock = new();
    private DateTime? _wakeRequestedAt;

    public bool IsDormant => _dormant;
    public event Func<Task>? Entered;
    public event Func<Task>? Exited;
    public event Func<Task>? WakeDeadlineExpired;

    public async Task Enter()
    {
        if (!_dormant.TrySet()) return;
        DisarmWake();
        if (Entered != null) await Entered();
    }

    public async Task Exit()
    {
        if (!_dormant.TryReset()) return;
        DisarmWake();
        if (Exited != null) await Exited();
    }

    public bool RequestWake()
    {
        lock (_wakeLock)
        {
            if (!_dormant || _wakeRequestedAt != null) return false;
            _wakeRequestedAt = _now();
            return true;
        }
    }

    private void DisarmWake()
    {
        lock (_wakeLock) _wakeRequestedAt = null;
    }

    // exposed for tests — the hosted loop calls this once a second
    internal async Task CheckWakeDeadline()
    {
        if (!DeadlineLapsed()) return;
        _log.LogWarning("Still dormant {Seconds}s after input arrived — leaving the relay so the master can reclaim its cursor", WakeDeadline.TotalSeconds);
        if (WakeDeadlineExpired != null) await WakeDeadlineExpired();
    }

    private bool DeadlineLapsed()
    {
        lock (_wakeLock)
        {
            if (!_dormant || _wakeRequestedAt is not { } requestedAt) return false;
            if (_now() - requestedAt < WakeDeadline) return false;
            _wakeRequestedAt = null;
            return true;
        }
    }

    protected override Task Execute(CancellationToken cancel) => CheckWakeDeadline();
}
