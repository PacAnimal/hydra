using Cathedral.Logging;
using Cathedral.Utils;
using Hydra.Config;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Styx;
using Styx.Services;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Hydra.Relay;

public class EmbeddedStyxServer(EmbeddedStyxServerConfig config, ILogger<EmbeddedStyxServer> log)
    : SimpleHostedService(log)
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IClientRegistry? _registry;

    // completes when the relay is listening and faults when it could not start; always one or the other
    public Task WaitForReady() => _ready.Task;
    internal ValueTask<IReadOnlyList<ClientIdentity>> GetClients() => _registry?.GetAllIdentities()
        ?? ValueTask.FromResult<IReadOnlyList<ClientIdentity>>([]);

    protected override async Task Execute(CancellationToken cancel)
    {
        // built inside the try, since an out-of-range port throws while the listener is described
        WebApplication? app = null;
        var started = false;
        try
        {
            app = BuildApp();
            if (log.IsEnabled(LogLevel.Information))
                log.LogInformation("Starting embedded Styx relay on port {Port}", config.Port);
            await app.StartAsync(cancel);
            started = true;
            _ready.TrySetResult();
            if (log.IsEnabled(LogLevel.Information))
                log.LogInformation("Embedded Styx relay listening on port {Port}", config.Port);
            try { await Task.Delay(Timeout.Infinite, cancel); }
            catch (OperationCanceledException) { }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // stopped before listening: a WaitForReady caller is still owed an answer
            _ready.TrySetCanceled(cancel);
            throw;
        }
        catch (Exception ex)
        {
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

            // any path that reached neither success nor a catch
            _ready.TrySetException(new InvalidOperationException("Embedded Styx relay stopped before it was ready"));
        }
    }

    // what times the hub's response throttle, so a test need not wait out a second per login
    internal TimeProvider ThrottleClock { get; init; } = TimeProvider.System;

    /// <summary>
    /// Internal so a test can compose this host's options without starting it — it is the SECOND relay, and
    /// what it configures its hub with is invisible from the outside otherwise.
    /// </summary>
    internal WebApplication BuildApp()
    {
        var builder = WebApplication.CreateBuilder();

        // nothing proxies a Hydra's own relay, so the address a peer connects from is the only one it has
        builder.AddStyxRelay(StyxAuth.V1Only(config.Password), new StyxOptions(false), behindProxy: false, ThrottleClock);
        builder.Services.AddSereneConsoleLogging();
        // the container makes the cert, so the app owns and disposes it, started or not; on Windows it holds a key container
        builder.Services.AddSingleton(_ => EmbeddedRelayTls.CreateCertificate());
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.IPv6Any, config.Port, listenOptions =>
            listenOptions.UseTcpNoDelay().UseHttps(options.ApplicationServices.GetRequiredService<X509Certificate2>())));

        var app = builder.Build();
        var proofs = EmbeddedRelayTls.ProofsFor(config.Password, EmbeddedRelayTls.FingerprintOf(app.Services.GetRequiredService<X509Certificate2>()));
        _registry = app.Services.GetRequiredService<IClientRegistry>();
        app.MapStyxRelay();
        var gate = new BindingGate(app.Services.GetRequiredService<ResponseThrottle>(), app.Services.GetRequiredService<FailureLimiter>());
        app.MapPost(EmbeddedRelayTls.BindingPath, (HttpContext http, CancellationToken cancel) => AnswerBinding(proofs, gate, http, cancel));
        return app;
    }

    // the server's proof goes only to a client that proved the password for this cert, so a stranger has nothing to guess
    // against; the gate decides how fast anyone may try
    private static async Task<IResult> AnswerBinding(EmbeddedRelayTls.Proofs proofs, BindingGate gate, HttpContext http, CancellationToken cancel)
    {
        var remoteIp = ClientRegistry.RemoteIpOf(http);
        var given = new byte[EmbeddedRelayTls.ProofLength + 1];
        var length = await http.Request.Body.ReadAtLeastAsync(given, given.Length, throwOnEndOfStream: false, cancel);

        var verdict = await gate.Check(remoteIp,
            () => length == EmbeddedRelayTls.ProofLength && CryptographicOperations.FixedTimeEquals(given.AsSpan(0, length), proofs.Client), cancel);
        return verdict switch
        {
            BindingVerdict.Proven => Results.Bytes(proofs.Server),
            BindingVerdict.Busy => Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
            _ => Results.Unauthorized(),
        };
    }
}
