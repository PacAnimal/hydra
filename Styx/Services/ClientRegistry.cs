using Cathedral.Extensions;
using Cathedral.Utils;

namespace Styx.Services;

public interface IClientRegistry
{
    ValueTask Register(string connectionId, Guid networkId, string hostName);
    ValueTask Unregister(string connectionId);
    ValueTask<string?> GetConnectionId(Guid networkId, string hostName);
    ValueTask<ClientIdentity?> GetIdentity(string connectionId);
    // returns the old connectionId if a duplicate was kicked, null otherwise
    ValueTask<string?> KickDuplicate(Guid networkId, string hostName, string newConnectionId);
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

    // finds any existing connection with the same network+hostname, removes it, returns its connectionId
    public async ValueTask<string?> KickDuplicate(Guid networkId, string hostName, string newConnectionId)
    {
        using var clients = await _clients.WaitForDisposable();
        string? found = null;
        foreach (var (connectionId, identity) in clients.Value)
        {
            if (identity.NetworkId == networkId
                && identity.HostName.EqualsIgnoreCase(hostName)
                && connectionId != newConnectionId)
            {
                found = connectionId;
                break;
            }
        }
        if (found is null) return null;
        clients.Value.Remove(found);
        log.LogInformation("Kicked duplicate {HostName} ({NetworkId}) on {ConnectionId}", hostName, networkId, found);
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
