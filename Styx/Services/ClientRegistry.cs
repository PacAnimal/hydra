using Cathedral.Extensions;
using Cathedral.Utils;
using Microsoft.Extensions.Logging;

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
    private readonly SemaphoreSlimValue<Dictionary<string, ClientData>> _clients = new([]);

    public async ValueTask Register(string connectionId, Guid networkId, string hostName)
    {
        using var clients = await _clients.WaitForDisposable();
        clients.Value[connectionId] = new ClientData(networkId, hostName, DateTime.UtcNow);
        log.LogInformation("Registered {HostName} ({NetworkId}) on {ConnectionId}", hostName, networkId, connectionId);
    }

    public async ValueTask Unregister(string connectionId)
    {
        using var clients = await _clients.WaitForDisposable();
        if (clients.Value.Remove(connectionId, out var data))
            log.LogInformation("Unregistered {HostName} on {ConnectionId}", data.HostName, connectionId);
    }

    public async ValueTask<string?> GetConnectionId(Guid networkId, string hostName)
    {
        using var clients = await _clients.WaitForDisposable();
        foreach (var (connectionId, data) in clients.Value)
        {
            if (data.NetworkId == networkId && data.HostName.EqualsIgnoreCase(hostName))
                return connectionId;
        }
        return null;
    }

    public async ValueTask<ClientIdentity?> GetIdentity(string connectionId)
    {
        using var clients = await _clients.WaitForDisposable();
        return clients.Value.TryGetValue(connectionId, out var data)
            ? new ClientIdentity(data.NetworkId, data.HostName)
            : null;
    }

    // finds any existing connection with the same network+hostname, removes it, returns its connectionId
    public async ValueTask<string?> KickDuplicate(Guid networkId, string hostName, string newConnectionId)
    {
        using var clients = await _clients.WaitForDisposable();
        foreach (var (connectionId, data) in clients.Value)
        {
            if (data.NetworkId == networkId
                && data.HostName.EqualsIgnoreCase(hostName)
                && connectionId != newConnectionId)
            {
                clients.Value.Remove(connectionId);
                log.LogInformation("Kicked duplicate {HostName} ({NetworkId}) on {ConnectionId}", hostName, networkId, connectionId);
                return connectionId;
            }
        }
        return null;
    }

    public async ValueTask<IReadOnlyList<(string ConnectionId, string HostName)>> GetNetworkClients(Guid networkId, string? excludeConnectionId = null)
    {
        using var clients = await _clients.WaitForDisposable();
        var result = new List<(string, string)>();
        foreach (var (connectionId, data) in clients.Value)
        {
            if (data.NetworkId == networkId && connectionId != excludeConnectionId)
                result.Add((connectionId, data.HostName));
        }
        return result;
    }

    private record ClientData(Guid NetworkId, string HostName, DateTime ConnectedAt);
}
