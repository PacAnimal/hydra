using System.Collections.Concurrent;
using Cathedral.Extensions;

namespace Styx.Services;

public interface IClientRegistry
{
    ValueTask Register(string connectionId, Guid networkId, string hostName, string remoteIp);
    ValueTask Unregister(string connectionId);
    ValueTask<string?> GetConnectionId(Guid networkId, string hostName);
    ValueTask<ClientIdentity?> GetIdentity(string connectionId);
    // atomically kicks same-network+host duplicates AND registers the new connection under one lock, so two
    // concurrent authenticates for the same host can't both find nothing to kick and both register.
    ValueTask<RegistrationResult> RegisterKickingDuplicates(string connectionId, Guid networkId, string hostName, string remoteIp);
    // returns all clients on a network, optionally excluding one connection
    ValueTask<IReadOnlyList<NetworkClient>> GetNetworkClients(Guid networkId, string? excludeConnectionId = null);
}

public record ClientIdentity(Guid NetworkId, string HostName, string RemoteIp);

public record NetworkClient(string ConnectionId, string HostName);

/// <param name="Kicked">connectionIds displaced by this registration — more than one if stale entries accumulated.</param>
/// <param name="OtherClients">the network with the duplicates gone but before this connection joined, so a
/// displaced host can be broadcast as having left before it is broadcast as having arrived.</param>
public record RegistrationResult(IReadOnlyList<string> Kicked, IReadOnlyList<NetworkClient> OtherClients);

public class ClientRegistry(ILogger<ClientRegistry> log) : IClientRegistry
{
    private readonly Lock _mutationLock = new();
    private readonly ConcurrentDictionary<string, ClientIdentity> _byConnection = [];
    private readonly ConcurrentDictionary<(Guid NetworkId, string HostName), string> _byNetworkHost = [];

    public ValueTask Register(string connectionId, Guid networkId, string hostName, string remoteIp)
    {
        lock (_mutationLock)
            Register(connectionId, new ClientIdentity(networkId, hostName, remoteIp));
        log.LogDebug("Registered client \"{HostName}\" from {RemoteIp} on network {NetworkId}", hostName, remoteIp, networkId);
        return ValueTask.CompletedTask;
    }

    public ValueTask Unregister(string connectionId)
    {
        ClientIdentity? identity;
        lock (_mutationLock)
            identity = Remove(connectionId);
        if (identity != null)
        {
            log.LogInformation("Unregistered client \"{HostName}\" from network {NetworkId}", identity.HostName, identity.NetworkId);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<string?> GetConnectionId(Guid networkId, string hostName) =>
        ValueTask.FromResult(_byNetworkHost.GetValueOrDefault(HostKey(networkId, hostName)));

    public ValueTask<ClientIdentity?> GetIdentity(string connectionId) =>
        ValueTask.FromResult(_byConnection.TryGetValue(connectionId, out var identity) ? identity : null);

    // atomically kick same-network+host duplicates and register the new connection under one lock
    public ValueTask<RegistrationResult> RegisterKickingDuplicates(string connectionId, Guid networkId, string hostName, string remoteIp)
    {
        RegistrationResult result;
        lock (_mutationLock)
        {
            var found = _byConnection
                .Where(kv => kv.Value.NetworkId == networkId
                    && kv.Value.HostName.EqualsOrdinal(hostName)
                    && kv.Key != connectionId)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var id in found)
            {
                Remove(id);
                log.LogInformation("Kicked duplicate \"{HostName}\" from network {NetworkId}", hostName, networkId);
            }
            var others = OnNetwork(networkId, connectionId);
            Register(connectionId, new ClientIdentity(networkId, hostName, remoteIp));
            result = new RegistrationResult(found, others);
        }
        log.LogDebug("Registered client \"{HostName}\" from {RemoteIp} on network {NetworkId}", hostName, remoteIp, networkId);
        return ValueTask.FromResult(result);
    }

    public ValueTask<IReadOnlyList<NetworkClient>> GetNetworkClients(Guid networkId, string? excludeConnectionId = null) =>
        ValueTask.FromResult<IReadOnlyList<NetworkClient>>(OnNetwork(networkId, excludeConnectionId));

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
        if (!_byConnection.TryRemove(connectionId, out var old)) return null;
        var key = HostKey(old.NetworkId, old.HostName);
        if (_byNetworkHost.GetValueOrDefault(key) == connectionId)
            _byNetworkHost.TryRemove(key, out _);
        return old;
    }

    private static string Normalize(string hostName) => hostName.ToLowerInvariant();
    private static (Guid NetworkId, string HostName) HostKey(Guid networkId, string hostName) =>
        (networkId, Normalize(hostName));
}
