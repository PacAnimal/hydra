using Cathedral.Extensions;
using Cathedral.Utils;

namespace Styx.Services;

public interface IClientRegistry
{
    ValueTask Register(string connectionId, Guid networkId, string hostName, string remoteIp);
    ValueTask Unregister(string connectionId);
    ValueTask<string?> GetConnectionId(Guid networkId, string hostName);
    ValueTask<ClientIdentity?> GetIdentity(string connectionId);
    // atomically kicks same-network+host duplicates AND registers the new connection under one lock, so two
    // concurrent authenticates for the same host can't both find nothing to kick and both register.
    // returns the kicked connectionIds (may be >1 if stale entries accumulated on unclean disconnect).
    ValueTask<IReadOnlyList<string>> RegisterKickingDuplicates(string connectionId, Guid networkId, string hostName, string remoteIp);
    // returns all (connectionId, hostName) pairs on a network, optionally excluding one connection
    ValueTask<IReadOnlyList<(string ConnectionId, string HostName)>> GetNetworkClients(Guid networkId, string? excludeConnectionId = null);
}

public record ClientIdentity(Guid NetworkId, string HostName, string RemoteIp);

public class ClientRegistry(ILogger<ClientRegistry> log) : IClientRegistry
{
    private readonly SemaphoreSlimValue<Dictionary<string, ClientIdentity>> _clients = new([]);

    public async ValueTask Register(string connectionId, Guid networkId, string hostName, string remoteIp)
    {
        using var clients = await _clients.WaitForDisposable();
        clients.Value[connectionId] = new ClientIdentity(networkId, hostName, remoteIp);
        log.LogDebug("Registered client \"{HostName}\" from {RemoteIp} on network {NetworkId}", hostName, remoteIp, networkId);
    }

    public async ValueTask Unregister(string connectionId)
    {
        using var clients = await _clients.WaitForDisposable();
        if (clients.Value.Remove(connectionId, out var identity))
            log.LogInformation("Unregistered client \"{HostName}\" from network {NetworkId}", identity.HostName, identity.NetworkId);
    }

    public async ValueTask<string?> GetConnectionId(Guid networkId, string hostName)
    {
        using var clients = await _clients.WaitForDisposable();
        foreach (var (connectionId, identity) in clients.Value)
        {
            if (identity.NetworkId == networkId && identity.HostName.EqualsOrdinal(hostName))
                return connectionId;
        }
        return null;
    }

    public async ValueTask<ClientIdentity?> GetIdentity(string connectionId)
    {
        using var clients = await _clients.WaitForDisposable();
        return clients.Value.TryGetValue(connectionId, out var identity) ? identity : null;
    }

    // atomically kick same-network+host duplicates and register the new connection under one lock
    public async ValueTask<IReadOnlyList<string>> RegisterKickingDuplicates(string connectionId, Guid networkId, string hostName, string remoteIp)
    {
        using var clients = await _clients.WaitForDisposable();
        var found = clients.Value
            .Where(kv => kv.Value.NetworkId == networkId
                && kv.Value.HostName.EqualsOrdinal(hostName)
                && kv.Key != connectionId)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var id in found)
        {
            clients.Value.Remove(id);
            log.LogInformation("Kicked duplicate \"{HostName}\" from network {NetworkId}", hostName, networkId);
        }
        clients.Value[connectionId] = new ClientIdentity(networkId, hostName, remoteIp);
        log.LogDebug("Registered client \"{HostName}\" from {RemoteIp} on network {NetworkId}", hostName, remoteIp, networkId);
        return found;
    }

    public async ValueTask<IReadOnlyList<(string ConnectionId, string HostName)>> GetNetworkClients(Guid networkId, string? excludeConnectionId = null)
    {
        using var clients = await _clients.WaitForDisposable();
        var result = new List<(string, string)>();
        foreach (var (connectionId, identity) in clients.Value)
        {
            if (identity.NetworkId == networkId && connectionId != excludeConnectionId)
                result.Add((connectionId, identity.HostName));
        }
        return result;
    }

}
