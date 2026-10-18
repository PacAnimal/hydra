using Cathedral.Utils;
using Microsoft.Extensions.Logging;

namespace Hydra.Config;

// A slave whose displays slept stays on the relay and refuses input instead of restarting into idle. The first
// refused input starts a deadline: match the profile again within it, or leave the relay. In-memory only.
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
    // how long a woken machine has to bring its displays back and match the profile again
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
