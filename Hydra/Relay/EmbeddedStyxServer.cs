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

    /// <summary>
    /// Completes when the relay is listening, and FAULTS when it could not start.
    ///
    /// <para><b>It must do one or the other, always.</b> This used to be set on success alone, so a start
    /// that threw — a port taken between being probed and being bound is the easy way — left every caller
    /// waiting on a task nothing would ever complete. That is not a slow start, it is a permanent one: the
    /// failure was logged by the hosted service and the awaiting test hung until the run was killed. Thirty
    /// four minutes of a CI lane, once.</para>
    /// </summary>
    public Task WaitForReady() => _ready.Task;
    internal ValueTask<IReadOnlyList<ClientIdentity>> GetClients() => _registry?.GetAllIdentities()
        ?? ValueTask.FromResult<IReadOnlyList<ClientIdentity>>([]);

    protected override async Task Execute(CancellationToken cancel)
    {
        // INSIDE the try, because building the app can fail on its own — an out-of-range port throws while
        // Kestrel's listener is being described, before anything is started. Left outside, that failure
        // skipped the catch below and put readiness right back where it was: waiting for ever.
        WebApplication? app = null;
        var started = false;
        try
        {
            app = BuildApp();
            log.LogInformation("Starting embedded Styx relay on port {Port}", config.Port);
            await app.StartAsync(cancel);
            started = true;
            _ready.TrySetResult();
            log.LogInformation("Embedded Styx relay listening on port {Port}", config.Port);
            try { await Task.Delay(Timeout.Infinite, cancel); }
            catch (OperationCanceledException) { }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // Shutting down before we ever listened. Nobody is owed a listening relay, but they are owed an
            // answer — a caller parked in WaitForReady must come back rather than outlive the service.
            _ready.TrySetCanceled(cancel);
            throw;
        }
        catch (Exception ex)
        {
            // THE ANSWER IS THE POINT. Whatever went wrong, every WaitForReady caller learns it here; the
            // alternative is the hang this replaced, where the failure was logged and the waiter simply
            // never returned.
            _ready.TrySetException(ex);
            throw;
        }
        finally
        {
            try
            {
                if (started && app != null)
                {
                    using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await app.StopAsync(stopTimeout.Token);
                }
            }
            catch (OperationCanceledException) { log.LogWarning("Embedded Styx did not stop within five seconds"); }
            catch (Exception ex) { log.LogWarning(ex, "Embedded Styx stop failed"); }
            finally { if (app != null) await app.DisposeAsync(); }

            // A last resort for a path that reached neither success nor a catch — a return this method does
            // not have today, or one added later. Cheap, and it makes "WaitForReady always completes" a
            // property of the method rather than of its current shape.
            _ready.TrySetException(new InvalidOperationException("Embedded Styx relay stopped before it was ready"));
        }
    }

    /// <summary>
    /// Internal so a test can compose this host's options without starting it — it is the SECOND relay, and
    /// what it configures its hub with is invisible from the outside otherwise.
    /// </summary>
    internal WebApplication BuildApp()
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
