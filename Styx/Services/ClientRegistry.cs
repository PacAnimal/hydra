using Cathedral.Extensions;

namespace Styx.Services;

public interface IClientRegistry
{
    // what was registered on the connection, if anything
    ClientIdentity? Unregister(string connectionId);
    string? GetConnectionId(NetworkKey network, string hostName);
    ClientIdentity? GetIdentity(string connectionId);
    // atomically kicks same-network+host duplicates AND registers the new connection under one lock, so two
    // concurrent authenticates for the same host can't both find nothing to kick and both register.
    RegistrationResult Register(string connectionId, NetworkKey network, string hostName, string remoteIp, string localIp);
    // removes the registration only if it is still the one with this generation, so a timer armed for an earlier
    // login can never evict whatever holds the connection or the name now
    ClientIdentity? UnregisterIfCurrent(string connectionId, long generation);
    IReadOnlyList<NetworkClient> GetNetworkClients(NetworkKey network);
    IReadOnlyList<ClientIdentity> GetAllIdentities();
}

/// <param name="Generation">unique per registration, so a later login on the same connection is told apart</param>
public record ClientIdentity(NetworkKey Network, string HostName, string RemoteIp, string LocalIp, long Generation);

public record NetworkClient(string ConnectionId, string HostName);

/// <param name="Kicked">the connection this registration displaced from the name, if any</param>
/// <param name="OtherClients">the network with the duplicates gone but before this connection joined, so a
/// displaced host can be broadcast as having left before it is broadcast as having arrived.</param>
/// <param name="Generation">the new registration's generation</param>
/// <param name="Left">the network this connection was registered on before, when it was a different one, so its
/// peers there can be told it has gone</param>
public record RegistrationResult(string? Kicked, IReadOnlyList<NetworkClient> OtherClients, long Generation, NetworkKey? Left);

public class ClientRegistry(ILogger<ClientRegistry> log) : IClientRegistry
{
    // stands in for an address the connection could not report
    public const string Unknown = "unknown";

    public static string RemoteIpOf(HttpContext? http) => http?.Connection.RemoteIpAddress?.ToString() ?? Unknown;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, ClientIdentity> _byConnection = [];
    private readonly Dictionary<HostKey, string> _byNetworkHost = [];
    private long _generation;

    public ClientIdentity? Unregister(string connectionId)
    {
        ClientIdentity? identity;
        lock (_lock)
            identity = Remove(connectionId);
        LogUnregistered(identity);
        return identity;
    }

    public ClientIdentity? UnregisterIfCurrent(string connectionId, long generation)
    {
        ClientIdentity? identity = null;
        lock (_lock)
        {
            if (_byConnection.TryGetValue(connectionId, out var current) && current.Generation == generation)
                identity = Remove(connectionId);
        }
        LogUnregistered(identity);
        return identity;
    }

    // the lock is held only for a lookup, so a relay send pays for an uncontended acquire at most
    public string? GetConnectionId(NetworkKey network, string hostName)
    {
        lock (_lock) return _byNetworkHost.GetValueOrDefault(new HostKey(network, hostName));
    }

    public ClientIdentity? GetIdentity(string connectionId)
    {
        lock (_lock) return _byConnection.GetValueOrDefault(connectionId);
    }

    public RegistrationResult Register(string connectionId, NetworkKey network, string hostName, string remoteIp, string localIp)
    {
        RegistrationResult result;
        lock (_lock)
        {
            string? kicked = null;
            if (_byNetworkHost.TryGetValue(new HostKey(network, hostName), out var previousConnectionId)
                && previousConnectionId != connectionId)
            {
                kicked = previousConnectionId;
                Remove(previousConnectionId);
                if (log.IsEnabled(LogLevel.Information))
                    log.LogInformation("Kicked duplicate \"{HostName}\" from network {Network}", hostName, network);
            }
            var left = _byConnection.TryGetValue(connectionId, out var before) && before.Network != network ? before.Network : (NetworkKey?)null;
            var others = OnNetwork(network, connectionId);
            var generation = ++_generation;
            Index(connectionId, new ClientIdentity(network, hostName, remoteIp, localIp, generation));
            result = new RegistrationResult(kicked, others, generation, left);
        }
        if (log.IsEnabled(LogLevel.Debug))
            log.LogDebug("Registered client \"{HostName}\" from {RemoteIp} on network {Network}", hostName, remoteIp, network);
        return result;
    }

    public IReadOnlyList<NetworkClient> GetNetworkClients(NetworkKey network)
    {
        lock (_lock) return OnNetwork(network, null);
    }

    public IReadOnlyList<ClientIdentity> GetAllIdentities()
    {
        lock (_lock) return [.. _byConnection.Values];
    }

    private List<NetworkClient> OnNetwork(NetworkKey network, string? excludeConnectionId)
    {
        var result = new List<NetworkClient>();
        foreach (var (connectionId, identity) in _byConnection)
        {
            if (identity.Network == network && connectionId != excludeConnectionId)
                result.Add(new NetworkClient(connectionId, identity.HostName));
        }
        return result;
    }

    private void LogUnregistered(ClientIdentity? identity)
    {
        if (identity != null && log.IsEnabled(LogLevel.Information))
            log.LogInformation("Unregistered client \"{HostName}\" from network {Network}", identity.HostName, identity.Network);
    }

    private void Index(string connectionId, ClientIdentity identity)
    {
        // a connection that logs in again drops the name it held before
        Remove(connectionId);
        _byConnection[connectionId] = identity;
        _byNetworkHost[new HostKey(identity.Network, identity.HostName)] = connectionId;
    }

    private ClientIdentity? Remove(string connectionId)
    {
        if (!_byConnection.Remove(connectionId, out var old)) return null;
        var key = new HostKey(old.Network, old.HostName);
        if (_byNetworkHost.GetValueOrDefault(key) == connectionId)
            _byNetworkHost.Remove(key);
        return old;
    }

    // a name compares ignoring case, as a token's hostname caveats do: a Send names its targets in the sender's own
    // spelling, and a name a caveat excludes can never reach the excluded peer's slot through another case relation
    private readonly record struct HostKey(NetworkKey Network, string HostName)
    {
        public bool Equals(HostKey other) => Network == other.Network && HostName.EqualsIgnoreCase(other.HostName);

        public override int GetHashCode() => HashCode.Combine(Network, StringComparer.OrdinalIgnoreCase.GetHashCode(HostName));
    }
}
