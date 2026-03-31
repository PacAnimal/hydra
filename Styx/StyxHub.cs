using Cathedral.Utils;
using Common.DTO;
using Common.Interfaces;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Styx.Filters;
using Styx.Services;

namespace Styx;

public class StyxHub(IClientRegistry registry, ILogger<StyxHub> log) : Hub<IStyxClient>, IStyxServer
{
    [AllowAnonymousHub]
    public async Task<RelayLoginResponse> Authenticate(RelayLogin login)
    {
        var password = Environment.GetEnvironmentVariable("RELAY_PASSWORD");
        if (string.IsNullOrEmpty(password))
        {
            log.LogError("RELAY_PASSWORD is not set");
            return new RelayLoginResponse { Authenticated = false, Message = "Server misconfigured" };
        }

        Guid networkId;
        try
        {
            networkId = await new SimpleAes(password).DecryptBase64<Guid>(login.Authorization, true, Context.ConnectionAborted);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Authentication failed for {HostName}", login.HostName);
            return new RelayLoginResponse { Authenticated = false, Message = "Invalid authorization" };
        }

        // kick any existing connection with the same network+hostname
        var kicked = await registry.KickDuplicate(networkId, login.HostName, Context.ConnectionId);
        if (kicked != null)
            await Clients.Client(kicked).Kicked("duplicate hostname");

        await registry.Register(Context.ConnectionId, networkId, login.HostName);
        log.LogInformation("Authenticated {HostName} on network {NetworkId}", login.HostName, networkId);
        await BroadcastPeers(networkId);
        return new RelayLoginResponse { Authenticated = true };
    }

    [AllowAnonymousHub]
    public Task<bool> Ping() => Task.FromResult(true);

    public async Task Send(string targetHost, byte[] payload)
    {
        var identity = await registry.GetIdentity(Context.ConnectionId);
        if (identity == null) return;

        var targetConnectionId = await registry.GetConnectionId(identity.NetworkId, targetHost);
        if (targetConnectionId == null)
        {
            log.LogDebug("Target {TargetHost} not found on network {NetworkId}", targetHost, identity.NetworkId);
            return;
        }

        await Clients.Client(targetConnectionId).Receive(identity.HostName, payload);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var identity = await registry.GetIdentity(Context.ConnectionId);
        await registry.Unregister(Context.ConnectionId);
        if (identity != null)
            await BroadcastPeers(identity.NetworkId);
        await base.OnDisconnectedAsync(exception);
    }

    // sends each client in the network its current peer list (excluding itself)
    private async Task BroadcastPeers(Guid networkId)
    {
        var clients = await registry.GetNetworkClients(networkId);
        var allHostNames = clients.Select(c => c.HostName).ToArray();
        foreach (var (connectionId, hostName) in clients)
        {
            var peers = allHostNames.Where(h => h != hostName).ToArray();
            await Clients.Client(connectionId).Peers(peers);
        }
    }
}
