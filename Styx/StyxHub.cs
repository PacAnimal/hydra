using Cathedral.Utils;
using Common.DTO;
using Common.Interfaces;
using Microsoft.AspNetCore.SignalR;
using Styx.Filters;
using Styx.Services;

namespace Styx;

public class StyxHub(IClientRegistry registry, IPeerBroadcaster peers, IStyxPasswordProvider passwordProvider, ILogger<StyxHub> log) : Hub<IStyxClient>, IStyxServer
{
    [AllowAnonymousHub]
    public async Task<RelayLoginResponse> Authenticate(RelayLogin login)
    {
        // throttle — minimum response time regardless of outcome
        var throttle = Task.Delay(TimeSpan.FromSeconds(Constants.AuthThrottleSeconds), Context.ConnectionAborted);

        string password;
        try
        {
            password = passwordProvider.Password;
        }
        catch (InvalidOperationException ex)
        {
            log.LogError("Relay password unavailable: {Message}", ex.Message);
            await throttle;
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
            await throttle;
            return new RelayLoginResponse { Authenticated = false, Message = "Invalid authorization" };
        }

        // kick any existing connections with the same network+hostname (stale entries can accumulate on unclean disconnect)
        var kicked = await registry.KickDuplicates(networkId, login.HostName, Context.ConnectionId);
        foreach (var connectionId in kicked)
            await Clients.Client(connectionId).Kicked("duplicate hostname");

        await registry.Register(Context.ConnectionId, networkId, login.HostName);
        log.LogInformation("Authenticated {HostName} on network {NetworkId}", login.HostName, networkId);
        await throttle;

        // queue after throttle so Authenticated=true is sent to the caller before Peers arrives
        peers.QueueBroadcast(networkId);
        return new RelayLoginResponse { Authenticated = true };
    }

    [AllowAnonymousHub]
    public Task<bool> Ping() => Task.FromResult(true);

    public async Task Send(string[] targetHosts, byte[] payload)
    {
        if (targetHosts.Length == 0)
        {
            log.LogError("Send called with empty targetHosts array");
            return;
        }

        var identity = await registry.GetIdentity(Context.ConnectionId);
        if (identity == null) return;

        foreach (var targetHost in targetHosts)
        {
            if (string.IsNullOrEmpty(targetHost))
            {
                log.LogError("Send called with empty hostname in targetHosts");
                continue;
            }

            var targetConnectionId = await registry.GetConnectionId(identity.NetworkId, targetHost);
            if (targetConnectionId == null)
            {
                log.LogDebug("Target {TargetHost} not found on network {NetworkId}", targetHost, identity.NetworkId);
                continue;
            }

            await Clients.Client(targetConnectionId).Receive(identity.HostName, payload);
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var identity = await registry.GetIdentity(Context.ConnectionId);
        await registry.Unregister(Context.ConnectionId);
        if (identity != null)
            peers.QueueBroadcast(identity.NetworkId);
        await base.OnDisconnectedAsync(exception);
    }
}
