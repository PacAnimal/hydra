using Cathedral.Extensions;
using Cathedral.Logging;
using Cathedral.Utils;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.SignalR;
using Styx.Filters;
using Styx.Services;

namespace Styx;

public static class StyxRelayExtensions
{
    // everything a relay host needs besides its listeners, logging and any endpoints of its own, shared by
    // the standalone server and the one embedded in Hydra
    public static WebApplicationBuilder AddStyxRelay(this WebApplicationBuilder builder, IStyxPasswordProvider passwordProvider, StyxOptions options,
        TimeProvider? throttleClock = null)
    {
        var services = builder.Services;

        builder.DisableEventLog();
        services.AddDataProtection().PersistKeysToNowhere();
        services.AddStyxSignalR();

        services.AddSingleton(options);
        services.AddSingleton(new ResponseThrottle(throttleClock ?? TimeProvider.System));
        services.AddSingleton<IClientRegistry, ClientRegistry>();
        services.AddHostedService<IPeerBroadcaster, PeerBroadcastService>();
        services.AddSingleton(passwordProvider);
        services.AddSingleton<AuthenticationHubFilter>();
        services.Configure<HubOptions>(hub => hub.AddFilter<AuthenticationHubFilter>());

        services.AddCathedralForwardedHeaders();
        return builder;
    }

    // relay traffic is small input frames that must leave at once, not wait for Nagle to coalesce them
    public static ListenOptions UseTcpNoDelay(this ListenOptions listenOptions)
    {
        listenOptions.Use(next => ctx =>
        {
            var socketFeature = ctx.Features.Get<IConnectionSocketFeature>();
            if (socketFeature != null) socketFeature.Socket.NoDelay = true;
            return next(ctx);
        });
        return listenOptions;
    }
}
