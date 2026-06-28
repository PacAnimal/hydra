using Cathedral.Utils;
using Common.DTO;
using Common.Interfaces;
using Microsoft.AspNetCore.SignalR;
using Styx.Filters;
using Styx.Services;

namespace Styx;

public class StyxHub(IClientRegistry registry, IPeerBroadcaster peers, IStyxPasswordProvider passwordProvider, ILogger<StyxHub> log, StyxOptions options) : Hub<IStyxClient>, IStyxServer
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

        var remoteIp = RemoteIp;

        Guid networkId;
        try
        {
            networkId = await new SimpleAes(password).DecryptBase64<Guid>(login.Authorization, true, Context.ConnectionAborted);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Authentication failed for \"{HostName}\" from {RemoteIp}", login.HostName.ToLowerInvariant(), remoteIp);
            await throttle;
            return new RelayLoginResponse { Authenticated = false, Message = "Invalid authorization" };
        }

        var hostName = login.HostName.ToLowerInvariant();

        // kick any existing connections with the same network+hostname (stale entries can accumulate on unclean disconnect)
        var kicked = await registry.KickDuplicates(networkId, hostName, Context.ConnectionId);
        foreach (var connectionId in kicked)
            await Clients.Client(connectionId).Kicked("duplicate hostname");

        await registry.Register(Context.ConnectionId, networkId, hostName, remoteIp);
        log.LogInformation("Authentication accepted for \"{HostName}\" (connectionId:{ConnectionId}) from {RemoteIp} on network {NetworkId}", hostName, Context.ConnectionId, remoteIp, networkId);
        await throttle;

        // queue after throttle so Authenticated=true is sent to the caller before Peers arrives
        peers.QueueBroadcast(networkId);
        return new RelayLoginResponse { Authenticated = true };
    }

    [AllowAnonymousHub]
    public Task<bool> Ping() => Task.FromResult(true);

    public Task<string> GetMyIp() => Task.FromResult(RemoteIp);

    public async Task Send(string[] targetHosts, byte[] payload)
    {
        if (targetHosts.Length == 0)
        {
            log.LogError("Send with empty targetHosts from (connectionId:{ConnectionId})", Context.ConnectionId);
            return;
        }

        var identity = await registry.GetIdentity(Context.ConnectionId);
        if (identity == null) return;

        if (options.DebugMessages)
            log.LogInformation("MSG net={NetworkId} {Sender} → [{Targets}] {Size}B",
                identity.NetworkId, identity.HostName, string.Join(", ", targetHosts), payload.Length);

        foreach (var targetHost in targetHosts)
        {
            if (string.IsNullOrEmpty(targetHost))
            {
                log.LogError("Send from \"{HostName}\" on network {NetworkId} had empty hostname in targetHosts", identity.HostName, identity.NetworkId);
                continue;
            }

            var targetConnectionId = await registry.GetConnectionId(identity.NetworkId, targetHost.ToLowerInvariant());
            if (targetConnectionId == null)
            {
                log.LogDebug("Target {TargetHost} not found on network {NetworkId}", targetHost, identity.NetworkId);
                continue;
            }

            await Clients.Client(targetConnectionId).Receive(identity.HostName, identity.RemoteIp, payload);
        }
    }

    private string RemoteIp => Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var identity = await registry.GetIdentity(Context.ConnectionId);
        await registry.Unregister(Context.ConnectionId);
        if (identity != null)
            peers.QueueBroadcast(identity.NetworkId);
        await base.OnDisconnectedAsync(exception);
    }
}
