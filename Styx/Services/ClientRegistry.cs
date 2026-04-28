using Cathedral.Extensions;
using Cathedral.Utils;

namespace Styx.Services;

public interface IClientRegistry
{
    ValueTask Register(string connectionId, Guid networkId, string hostName);
    ValueTask Unregister(string connectionId);
    ValueTask<string?> GetConnectionId(Guid networkId, string hostName);
    ValueTask<ClientIdentity?> GetIdentity(string connectionId);
    // returns all connectionIds that were kicked (may be >1 if stale entries accumulated)
    ValueTask<IReadOnlyList<string>> KickDuplicates(Guid networkId, string hostName, string newConnectionId);
    // returns all (connectionId, hostName) pairs on a network, optionally excluding one connection
    ValueTask<IReadOnlyList<(string ConnectionId, string HostName)>> GetNetworkClients(Guid networkId, string? excludeConnectionId = null);
}

public record ClientIdentity(Guid NetworkId, string HostName);

public class ClientRegistry(ILogger<ClientRegistry> log) : IClientRegistry
{
    private readonly SemaphoreSlimValue<Dictionary<string, ClientIdentity>> _clients = new([]);

    public async ValueTask Register(string connectionId, Guid networkId, string hostName)
    {
        using var clients = await _clients.WaitForDisposable();
        clients.Value[connectionId] = new ClientIdentity(networkId, hostName);
        log.LogInformation("Registered {HostName} ({NetworkId}) on {ConnectionId}", hostName, networkId, connectionId);
    }

    public async ValueTask Unregister(string connectionId)
    {
        using var clients = await _clients.WaitForDisposable();
        if (clients.Value.Remove(connectionId, out var identity))
            log.LogInformation("Unregistered {HostName} on {ConnectionId}", identity.HostName, connectionId);
    }

    public async ValueTask<string?> GetConnectionId(Guid networkId, string hostName)
    {
        using var clients = await _clients.WaitForDisposable();
        foreach (var (connectionId, identity) in clients.Value)
        {
            if (identity.NetworkId == networkId && identity.HostName.EqualsIgnoreCase(hostName))
                return connectionId;
        }
        return null;
    }

    public async ValueTask<ClientIdentity?> GetIdentity(string connectionId)
    {
        using var clients = await _clients.WaitForDisposable();
        return clients.Value.TryGetValue(connectionId, out var identity) ? identity : null;
    }

    // finds all existing connections with the same network+hostname, removes them, returns their connectionIds
    public async ValueTask<IReadOnlyList<string>> KickDuplicates(Guid networkId, string hostName, string newConnectionId)
    {
        using var clients = await _clients.WaitForDisposable();
        var found = clients.Value
            .Where(kv => kv.Value.NetworkId == networkId
                && kv.Value.HostName.EqualsIgnoreCase(hostName)
                && kv.Key != newConnectionId)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var connectionId in found)
        {
            clients.Value.Remove(connectionId);
            log.LogInformation("Kicked duplicate {HostName} ({NetworkId}) on {ConnectionId}", hostName, networkId, connectionId);
        }
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
