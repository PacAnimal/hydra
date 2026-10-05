namespace Styx.Services;

public interface IClientRegistry
{
    ValueTask Register(string connectionId, Guid networkId, string hostName, string remoteIp, string localIp = ClientRegistry.Unknown);
    ValueTask Unregister(string connectionId);
    ValueTask<string?> GetConnectionId(Guid networkId, string hostName);
    ValueTask<ClientIdentity?> GetIdentity(string connectionId);
    // atomically kicks same-network+host duplicates AND registers the new connection under one lock, so two
    // concurrent authenticates for the same host can't both find nothing to kick and both register.
    ValueTask<RegistrationResult> RegisterKickingDuplicates(string connectionId, Guid networkId, string hostName, string remoteIp, string localIp = ClientRegistry.Unknown);
    // returns all clients on a network, optionally excluding one connection
    ValueTask<IReadOnlyList<NetworkClient>> GetNetworkClients(Guid networkId, string? excludeConnectionId = null);
    ValueTask<IReadOnlyList<ClientIdentity>> GetAllIdentities();
}

public record ClientIdentity(Guid NetworkId, string HostName, string RemoteIp, string LocalIp);

public record NetworkClient(string ConnectionId, string HostName);

/// <param name="Kicked">connectionIds displaced by this registration — more than one if stale entries accumulated.</param>
/// <param name="OtherClients">the network with the duplicates gone but before this connection joined, so a
/// displaced host can be broadcast as having left before it is broadcast as having arrived.</param>
public record RegistrationResult(IReadOnlyList<string> Kicked, IReadOnlyList<NetworkClient> OtherClients);

public class ClientRegistry(ILogger<ClientRegistry> log) : IClientRegistry
{
    // stands in for an address the connection could not report
    public const string Unknown = "unknown";

    private readonly Lock _lock = new();
    private readonly Dictionary<string, ClientIdentity> _byConnection = [];
    private readonly Dictionary<(Guid NetworkId, string HostName), string> _byNetworkHost = [];

    public ValueTask Register(string connectionId, Guid networkId, string hostName, string remoteIp, string localIp = Unknown)
    {
        lock (_lock)
            Register(connectionId, new ClientIdentity(networkId, hostName, remoteIp, localIp));
        if (log.IsEnabled(LogLevel.Debug))
            log.LogDebug("Registered client \"{HostName}\" from {RemoteIp} on network {NetworkId}", hostName, remoteIp, networkId);
        return ValueTask.CompletedTask;
    }

    public ValueTask Unregister(string connectionId)
    {
        ClientIdentity? identity;
        lock (_lock)
            identity = Remove(connectionId);
        if (identity != null && log.IsEnabled(LogLevel.Information))
        {
            log.LogInformation("Unregistered client \"{HostName}\" from network {NetworkId}", identity.HostName, identity.NetworkId);
        }
        return ValueTask.CompletedTask;
    }

    // the lock is held only for a lookup, so a relay send pays for an uncontended acquire at most
    public ValueTask<string?> GetConnectionId(Guid networkId, string hostName)
    {
        lock (_lock) return ValueTask.FromResult(_byNetworkHost.GetValueOrDefault(HostKey(networkId, hostName)));
    }

    public ValueTask<ClientIdentity?> GetIdentity(string connectionId)
    {
        lock (_lock) return ValueTask.FromResult(_byConnection.GetValueOrDefault(connectionId));
    }

    // atomically kick same-network+host duplicates and register the new connection under one lock
    public ValueTask<RegistrationResult> RegisterKickingDuplicates(string connectionId, Guid networkId, string hostName, string remoteIp, string localIp = Unknown)
    {
        RegistrationResult result;
        lock (_lock)
        {
            // Look up the same case-insensitive key Register() below will evict by — a case-sensitive
            // scan here could miss a duplicate that Register() then silently displaces without it ever
            // appearing in Kicked, leaving the displaced connection never told it lost the name.
            var found = new List<string>();
            if (_byNetworkHost.TryGetValue(HostKey(networkId, hostName), out var previousConnectionId)
                && previousConnectionId != connectionId)
            {
                found.Add(previousConnectionId);
                Remove(previousConnectionId);
                if (log.IsEnabled(LogLevel.Information))
                    log.LogInformation("Kicked duplicate \"{HostName}\" from network {NetworkId}", hostName, networkId);
            }
            var others = OnNetwork(networkId, connectionId);
            Register(connectionId, new ClientIdentity(networkId, hostName, remoteIp, localIp));
            result = new RegistrationResult(found, others);
        }
        if (log.IsEnabled(LogLevel.Debug))
            log.LogDebug("Registered client \"{HostName}\" from {RemoteIp} on network {NetworkId}", hostName, remoteIp, networkId);
        return ValueTask.FromResult(result);
    }

    public ValueTask<IReadOnlyList<NetworkClient>> GetNetworkClients(Guid networkId, string? excludeConnectionId = null)
    {
        lock (_lock) return ValueTask.FromResult<IReadOnlyList<NetworkClient>>(OnNetwork(networkId, excludeConnectionId));
    }

    public ValueTask<IReadOnlyList<ClientIdentity>> GetAllIdentities()
    {
        lock (_lock) return ValueTask.FromResult<IReadOnlyList<ClientIdentity>>([.. _byConnection.Values]);
    }

    private List<NetworkClient> OnNetwork(Guid networkId, string? excludeConnectionId)
    {
        var result = new List<NetworkClient>();
        foreach (var (connectionId, identity) in _byConnection)
        {
            if (identity.NetworkId == networkId && connectionId != excludeConnectionId)
                result.Add(new NetworkClient(connectionId, identity.HostName));
        }
        return result;
    }

    private void Register(string connectionId, ClientIdentity identity)
    {
        // A connection can re-authenticate in tests/third-party clients. Remove its previous reverse index
        // before assigning the new identity so the O(1) host index never points at stale connection data.
        Remove(connectionId);
        var hostKey = HostKey(identity.NetworkId, identity.HostName);
        if (_byNetworkHost.TryGetValue(hostKey, out var previousConnectionId))
            Remove(previousConnectionId);
        _byConnection[connectionId] = identity;
        _byNetworkHost[hostKey] = connectionId;
    }

    private ClientIdentity? Remove(string connectionId)
    {
        if (!_byConnection.Remove(connectionId, out var old)) return null;
        var key = HostKey(old.NetworkId, old.HostName);
        if (_byNetworkHost.GetValueOrDefault(key) == connectionId)
            _byNetworkHost.Remove(key);
        return old;
    }

    private static string Normalize(string hostName) => hostName.ToLowerInvariant();
    private static (Guid NetworkId, string HostName) HostKey(Guid networkId, string hostName) =>
        (networkId, Normalize(hostName));
}
