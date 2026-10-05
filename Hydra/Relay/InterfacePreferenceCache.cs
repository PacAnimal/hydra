using System.Net.NetworkInformation;

namespace Hydra.Relay;

/// <summary>
/// The OS's interface preferences, asked once and shared by every dial until the network changes or the answer
/// outlives <see cref="Lifetime"/>. A metric or service-order edit changes no address, so no event reports it.
/// </summary>
internal sealed class InterfacePreferenceCache
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(1);

    private readonly Func<CancellationToken, Task<IReadOnlyDictionary<string, int>>> _query;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private Task<IReadOnlyDictionary<string, int>>? _answer;
    private long _askedAt;

    internal InterfacePreferenceCache(Func<CancellationToken, Task<IReadOnlyDictionary<string, int>>> query, TimeProvider time)
    {
        _query = query;
        _time = time;
    }

    internal static InterfacePreferenceCache Shared { get; } = CreateShared();

    internal async Task<IReadOnlyDictionary<string, int>> GetAsync(CancellationToken cancellationToken)
    {
        Task<IReadOnlyDictionary<string, int>> answer;
        lock (_lock)
        {
            if (_answer == null || _time.GetElapsedTime(_askedAt) >= Lifetime)
            {
                _askedAt = _time.GetTimestamp();
                // shared, so no one caller's token may cancel it; the query bounds itself
                _answer = _query(CancellationToken.None);
            }

            answer = _answer;
        }

        var preferences = await answer.WaitAsync(cancellationToken);
        if (preferences.Count == 0) Forget(answer);
        return preferences;
    }

    internal void Invalidate()
    {
        lock (_lock) _answer = null;
    }

    private void Forget(Task<IReadOnlyDictionary<string, int>> answer)
    {
        lock (_lock)
            if (_answer == answer) _answer = null;
    }

    // subscribing throws where the OS offers no change notifications (Linux without netlink); the lifetime then
    // bounds a stale answer on its own, rather than a failed type initialiser failing every dial
    internal static InterfacePreferenceCache Create(Func<CancellationToken, Task<IReadOnlyDictionary<string, int>>> query, TimeProvider time,
        Action<Action> subscribeToNetworkChanges)
    {
        var cache = new InterfacePreferenceCache(query, time);
        try
        {
            subscribeToNetworkChanges(cache.Invalidate);
        }
        catch (Exception e) when (e is NetworkInformationException or PlatformNotSupportedException)
        {
            // lifetime only
        }
        return cache;
    }

    private static InterfacePreferenceCache CreateShared() =>
        Create(RelayAddressPreference.QueryInterfacePreferencesAsync, TimeProvider.System, invalidate =>
        {
            NetworkChange.NetworkAddressChanged += (_, _) => invalidate();
            NetworkChange.NetworkAvailabilityChanged += (_, _) => invalidate();
        });
}
