using Common.DTO;
using Common.Interfaces;
using Microsoft.AspNetCore.SignalR;
using Styx.Filters;
using Styx.Services;

namespace Styx;

public class StyxHub(IClientRegistry registry, IPeerBroadcaster peers, ITokenVerifier verifier, IExpiryScheduler expiry, PeerWriter writer,
    ILogger<StyxHub> log, StyxOptions options, ResponseThrottle throttler) : Hub<IStyxClient>, IStyxServer
{
    // one login at a time per connection, whatever the path's width, so the throttle below caps it at one a second
    private static readonly object LoginGateKey = new();

    public override async Task OnConnectedAsync()
    {
        Context.Items[LoginGateKey] = new SemaphoreSlim(1, 1);
        writer.Track(Context);
        await base.OnConnectedAsync();
    }

    [AllowAnonymousHub]
    public async Task<RelayLoginResponse> Authenticate(RelayLogin? login)
    {
        var gate = (SemaphoreSlim)Context.Items[LoginGateKey]!;
        await gate.WaitAsync(Context.ConnectionAborted);
        try
        {
            return await Login(login);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<RelayLoginResponse> Login(RelayLogin? login)
    {
        // minimum response time regardless of outcome, which also caps the displacement churn of a re-login loop
        var throttle = throttler.Start(Constants.AuthThrottleSeconds, Context.ConnectionAborted);
        var remoteIp = RemoteIp;

        Task<RelayLoginResponse> Refuse(string? message) => ResponseThrottle.After(throttle, new RelayLoginResponse { Authenticated = false, Message = message });

        // a third-party client can send any shape of login it likes — refuse an incomplete one with an
        // answer it can act on, rather than failing somewhere in the paths below
        if (login is null || string.IsNullOrWhiteSpace(login.Authorization) || string.IsNullOrWhiteSpace(login.HostName))
        {
            log.LogWarning("Authentication rejected from {RemoteIp}: login is missing an authorization or a hostname", remoteIp);
            return await Refuse(RelayReasons.IncompleteLogin);
        }

        // the canonical form peers see the name in and address it by (Styx.md §2)
        var hostName = login.HostName.ToLowerInvariant();
        var verdict = await verifier.Verify(login.Authorization, remoteIp, Context.ConnectionAborted);
        if (verdict.Network is not { } network)
        {
            log.LogWarning("Authentication failed for \"{HostName}\" from {RemoteIp}: {Reason}", hostName, remoteIp, verdict.Failure);
            return await Refuse(verdict.Failure);
        }

        // the rules judge the name as sent, grammar included; one they do not allow is refused, never rewritten into one they do
        if (verdict.Rules is { } rules && !rules.Allows(login.HostName))
        {
            log.LogWarning("Authentication refused for \"{HostName}\" from {RemoteIp} on network {Network}: the token does not allow the name",
                hostName, remoteIp, network);
            return await Refuse(RelayReasons.HostNameNotAllowed);
        }

        var registration = await registry.Register(Context.ConnectionId, network, hostName, remoteIp, LocalIp);
        if (registration.Kicked is { } kicked)
        {
            expiry.Cancel(kicked);
            writer.Kick(kicked, RelayReasons.KickedDuplicateHostname);
        }

        // armed before the abort check below, whose cleanup then cancels it too
        if (verdict.Rules?.Expires is { } expires) expiry.Schedule(Context.ConnectionId, registration.Generation, expires);
        else expiry.Cancel(Context.ConnectionId);

        // if the connection aborted during auth, OnDisconnectedAsync may have already run its Unregister
        // (before we registered above) — clean up so we don't leave a stale entry for a dead connection
        if (Context.ConnectionAborted.IsCancellationRequested)
        {
            await registry.Unregister(Context.ConnectionId);
            expiry.Cancel(Context.ConnectionId);
            // we displaced someone and then died before announcing either half, so nothing has told the
            // other peers the host is gone — converge them on what the registry actually holds now
            if (registration.Kicked != null) peers.QueueBroadcast(network);
            if (registration.Left is { } abandoned) peers.QueueBroadcast(abandoned);
            return new RelayLoginResponse { Authenticated = false, Message = RelayReasons.ConnectionAborted };
        }

        if (log.IsEnabled(LogLevel.Information))
            log.LogInformation("Authentication accepted for \"{HostName}\" (connectionId:{ConnectionId}) from {RemoteIp} on network {Network}",
                hostName, Context.ConnectionId, remoteIp, network);
        await throttle;

        // a reconnecting host is seen leaving before it is seen arriving; see IPeerBroadcaster.BroadcastAndWait
        if (registration.Kicked != null)
            await peers.BroadcastAndWait(network, registration.OtherClients, Context.ConnectionAborted);

        // a connection that moved here from another network or path is gone from there
        if (registration.Left is { } left) peers.QueueBroadcast(left);

        // queue after throttle so Authenticated=true is sent to the caller before Peers arrives
        peers.QueueBroadcast(network);
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

        if (options.DebugMessages && log.IsEnabled(LogLevel.Information))
            log.LogInformation("MSG net={Network} {Sender} → [{Targets}] {Size}B",
                identity.Network, identity.HostName, string.Join(", ", targetHosts), payload.Length);

        var targets = new List<string>(targetHosts.Length);
        foreach (var targetHost in targetHosts)
        {
            if (string.IsNullOrEmpty(targetHost))
            {
                log.LogError("Send from \"{HostName}\" on network {Network} had empty hostname in targetHosts", identity.HostName, identity.Network);
                continue;
            }

            var targetConnectionId = await registry.GetConnectionId(identity.Network, targetHost);
            if (targetConnectionId == null)
            {
                if (log.IsEnabled(LogLevel.Debug)) log.LogDebug("Target {TargetHost} not found on network {Network}", targetHost, identity.Network);
                continue;
            }

            targets.Add(targetConnectionId);
        }
        await writer.Receive(targets, identity.HostName, identity.RemoteIp, payload);
    }

    private string RemoteIp => ClientRegistry.RemoteIpOf(Context.GetHttpContext());
    private string LocalIp => Context.GetHttpContext()?.Connection.LocalIpAddress?.ToString() ?? ClientRegistry.Unknown;

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        expiry.Cancel(Context.ConnectionId);
        writer.Forget(Context.ConnectionId);
        var identity = await registry.Unregister(Context.ConnectionId);
        if (identity != null)
            peers.QueueBroadcast(identity.Network);
        await base.OnDisconnectedAsync(exception);
    }
}
