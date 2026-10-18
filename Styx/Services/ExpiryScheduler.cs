namespace Styx.Services;

public interface IExpiryScheduler
{
    // arms (or re-arms) the eviction of this connection's registration at the token's expiry
    void Schedule(string connectionId, long generation, DateTimeOffset expires);
    void Cancel(string connectionId);
}

/// <summary>
/// Evicts a peer when its token's <c>exp</c> passes, as for a displacement: <c>Kicked("authorization expired")</c>, then
/// one membership broadcast. Each timer belongs to one registration (connection and generation) and evicts only if
/// that registration is still current, so a re-authentication or a displacement can never be cut off by a timer armed
/// for the login before it.
/// </summary>
public sealed class ExpiryScheduler(
    IClientRegistry registry,
    IPeerBroadcaster peers,
    PeerWriter writer,
    TimeProvider clock,
    ILogger<ExpiryScheduler> log) : IExpiryScheduler, IDisposable
{
    // a timer cannot be due more than ~49.7 days out, so a later expiry is reached in steps
    private static readonly TimeSpan MaxStep = TimeSpan.FromHours(24);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Armed> _armed = [];

    public void Schedule(string connectionId, long generation, DateTimeOffset expires)
    {
        lock (_lock)
        {
            if (_armed.Remove(connectionId, out var previous)) previous.Timer.Dispose();
            _armed[connectionId] = Arm(connectionId, generation, expires);
        }
    }

    public void Cancel(string connectionId)
    {
        lock (_lock)
        {
            if (_armed.Remove(connectionId, out var armed)) armed.Timer.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var armed in _armed.Values) armed.Timer.Dispose();
            _armed.Clear();
        }
    }

    private Armed Arm(string connectionId, long generation, DateTimeOffset expires)
    {
        var due = expires - clock.GetUtcNow();
        if (due < TimeSpan.Zero) due = TimeSpan.Zero;
        if (due > MaxStep) due = MaxStep;
        var timer = clock.CreateTimer(_ => FireUnobserved(connectionId, generation, expires), null, due, Timeout.InfiniteTimeSpan);
        return new Armed(generation, timer);
    }

    // a timer callback cannot await; Fire catches and logs everything itself
    private void FireUnobserved(string connectionId, long generation, DateTimeOffset expires) => _ = Fire(connectionId, generation, expires);

    private async Task Fire(string connectionId, long generation, DateTimeOffset expires)
    {
        lock (_lock)
        {
            if (!_armed.TryGetValue(connectionId, out var armed) || armed.Generation != generation) return;
            armed.Timer.Dispose();
            if (clock.GetUtcNow() < expires)
            {
                _armed[connectionId] = Arm(connectionId, generation, expires);
                return;
            }
            _armed.Remove(connectionId);
        }

        try
        {
            var identity = await registry.UnregisterIfCurrent(connectionId, generation);
            if (identity is null) return;
            if (log.IsEnabled(LogLevel.Information))
                log.LogInformation("Authorization of \"{HostName}\" on network {Network} expired", identity.HostName, identity.Network);
            peers.QueueBroadcast(identity.Network);
            writer.Kick(connectionId, RelayReasons.KickedExpired);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to evict an expired registration (connectionId:{ConnectionId})", connectionId);
        }
    }

    private sealed record Armed(long Generation, ITimer Timer);
}
