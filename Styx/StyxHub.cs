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
    public async Task<RelayLoginResponse> Authenticate(RelayLogin? login)
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

        // a third-party client can send any shape of login it likes — refuse an incomplete one with an
        // answer it can act on, rather than failing somewhere in the paths below
        if (login is null || string.IsNullOrWhiteSpace(login.Authorization) || string.IsNullOrWhiteSpace(login.HostName))
        {
            log.LogWarning("Authentication rejected from {RemoteIp}: login is missing an authorization or a hostname", remoteIp);
            await throttle;
            return new RelayLoginResponse { Authenticated = false, Message = "Authorization and hostName are both required" };
        }

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

        // kick same network+hostname duplicates and register atomically (one lock) so two concurrent
        // authenticates for the same host can't both register (stale phantom peer)
        var registration = await registry.RegisterKickingDuplicates(Context.ConnectionId, networkId, hostName, remoteIp);
        foreach (var connectionId in registration.Kicked)
            await Clients.Client(connectionId).Kicked("duplicate hostname");

        // if the connection aborted during auth, OnDisconnectedAsync may have already run its Unregister
        // (before we registered above) — clean up so we don't leave a stale entry for a dead connection
        if (Context.ConnectionAborted.IsCancellationRequested)
        {
            await registry.Unregister(Context.ConnectionId);
            // we displaced someone and then died before announcing either half, so nothing has told the
            // other peers the host is gone — converge them on what the registry actually holds now
            if (registration.Kicked.Count > 0) peers.QueueBroadcast(networkId);
            return new RelayLoginResponse { Authenticated = false, Message = "Connection aborted" };
        }

        log.LogInformation("Authentication accepted for \"{HostName}\" (connectionId:{ConnectionId}) from {RemoteIp} on network {NetworkId}", hostName, Context.ConnectionId, remoteIp, networkId);
        await throttle;

        // a reconnecting host has to be seen leaving before it is seen arriving, or the pair is an identical
        // membership list and reads as no change at all — a master then keeps stale geometry for it and never
        // re-sends the config it needs. Both halves go through the broadcaster's single-reader queue, in this
        // order, so every peer observes them that way round.
        if (registration.Kicked.Count > 0)
            peers.QueueBroadcast(networkId, registration.OtherClients);

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
