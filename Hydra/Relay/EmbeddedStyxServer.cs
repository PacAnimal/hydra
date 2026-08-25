using Cathedral.Extensions;
using Cathedral.Logging;
using Cathedral.Utils;
using Hydra.Config;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Styx;
using Styx.Filters;
using Styx.Services;
using System.Net;

namespace Hydra.Relay;

public class EmbeddedStyxServer(EmbeddedStyxServerConfig config, ILogger<EmbeddedStyxServer> log)
    : SimpleHostedService(log)
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IClientRegistry? _registry;

    public Task WaitForReady() => _ready.Task;
    internal ValueTask<IReadOnlyList<ClientIdentity>> GetClients() => _registry?.GetAllIdentities()
        ?? ValueTask.FromResult<IReadOnlyList<ClientIdentity>>([]);

    protected override async Task Execute(CancellationToken cancel)
    {
        var app = BuildApp();
        var started = false;
        try
        {
            log.LogInformation("Starting embedded Styx relay on port {Port}", config.Port);
            await app.StartAsync(cancel);
            started = true;
            _ready.TrySetResult();
            log.LogInformation("Embedded Styx relay listening on port {Port}", config.Port);
            try { await Task.Delay(Timeout.Infinite, cancel); }
            catch (OperationCanceledException) { }
        }
        finally
        {
            try
            {
                if (started)
                {
                    using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await app.StopAsync(stopTimeout.Token);
                }
            }
            catch (OperationCanceledException) { log.LogWarning("Embedded Styx did not stop within five seconds"); }
            catch (Exception ex) { log.LogWarning(ex, "Embedded Styx stop failed"); }
            finally { await app.DisposeAsync(); }
        }
    }

    private WebApplication BuildApp()
    {
        var builder = WebApplication.CreateBuilder();
        var services = builder.Services;

        builder.DisableEventLog();
        services.AddSereneConsoleLogging();

        services.AddDataProtection().PersistKeysToNowhere();
        services.AddStyxSignalR();

        services.AddSingleton(new StyxOptions(false));
        services.AddSingleton<IClientRegistry, ClientRegistry>();
        services.AddHostedService<IPeerBroadcaster, PeerBroadcastService>();
        services.AddSingleton<IStyxPasswordProvider>(new InlineStyxPasswordProvider(config.Password));
        services.AddSingleton<AuthenticationHubFilter>();
        services.Configure<HubOptions>(options => options.AddFilter<AuthenticationHubFilter>());

        services.AddCathedralForwardedHeaders();

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.IPv6Any, config.Port, listenOptions =>
            {
                listenOptions.Use(next => ctx =>
                {
                    var socketFeature = ctx.Features.Get<IConnectionSocketFeature>();
                    if (socketFeature != null) socketFeature.Socket.NoDelay = true;
                    return next(ctx);
                });
            });
        });

        var app = builder.Build();
        _registry = app.Services.GetRequiredService<IClientRegistry>();
        app.MapHub<StyxHub>("/relay");
        return app;
    }
}
