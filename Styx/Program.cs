using Cathedral.Config;
using Cathedral.Extensions;
using Cathedral.Logging;
using Cathedral.Styx;
using Cathedral.Utils;
using Styx;
using Styx.Services;
using System.Net;
using System.Security.Cryptography;
using System.Text;

if (args.Length > 0 && StyxTools.IsCommand(args[0]))
    return StyxTools.Run(args, Environment.GetEnvironmentVariable, Console.In, Console.Out, Console.Error);

var config = Env.Config;

StyxAuth auth;
try
{
    auth = StyxEnvironment.Read(Environment.GetEnvironmentVariable);
}
catch (StyxConfigurationException ex)
{
    Console.Error.WriteLine($"{ex.Message} — refusing to start");
    return 1;
}

var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;

services.AddSereneConsoleLogging(logging =>
{
    logging.TimestampFormat = "yyyy-MM-dd HH:mm:ss";
    logging.TimestampUtc = true;
    logging.FilterMicrosoftSpam = true;
});
services.ConfigureHttpJsonOptions(options => SaneJson.Configure(options.SerializerOptions));

var debugMessages = Environment.GetEnvironmentVariable(Constants.DebugMessagesEnvVar)?.EqualsIgnoreCase("true") ?? false;
builder.AddStyxRelay(auth, new StyxOptions(debugMessages), behindProxy: true);

var port = int.Parse(config.GetString("LOCAL_PORT", "5000"));
var localOnly = Environment.GetEnvironmentVariable(Constants.LocalOnlyEnvVar)?.EqualsIgnoreCase("true") ?? false;
builder.WebHost.ConfigureKestrel(options =>
{
    void ConfigureListener(IPAddress address) => options.Listen(address, port, listenOptions => listenOptions.UseTcpNoDelay());

    if (localOnly)
    {
        ConfigureListener(IPAddress.Loopback);
        ConfigureListener(IPAddress.IPv6Loopback);
    }
    else
    {
        ConfigureListener(IPAddress.IPv6Any);
    }
});

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHub<StyxHub>("/relay");

app.MapGet("/api/status", async (HttpContext http, ITokenVerifier verifier, IClientRegistry registry, ResponseThrottle throttler, CancellationToken ct) =>
{
    var throttle = throttler.Start(Constants.StatusThrottleSeconds, ct);

    var bearer = http.Request.Headers.Authorization.ToString();
    var token = bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? bearer["Bearer ".Length..] : "";
    var verdict = await verifier.Verify(token, RemoteIpOf(http), ct);
    if (verdict.Network is not { } network)
    {
        await throttle;
        return Results.Unauthorized();
    }

    // a valid token's holder is answered at once; the throttle only slows guessing
    var clients = await registry.GetNetworkClients(network);
    return Results.Ok(new StatusResponse([.. clients.Select(c => c.HostName)]));
});

app.MapPost("/api/network-config", async (HttpContext http, NetworkConfigRequest request, StyxAuth relayAuth, FailureLimiter limiter,
    ResponseThrottle throttler, CancellationToken ct) =>
{
    var throttle = throttler.Start(Constants.NetworkConfigThrottleSeconds, ct);

    // nothing ever issues a v1 token, so without a key, or without the password to guard it, there is nothing here
    if (!relayAuth.CreatesNetworks || relayAuth.V1Password is not { } password)
    {
        await throttle;
        return Results.NotFound();
    }

    var remoteIp = RemoteIpOf(http);
    if (limiter.IsBlocked(remoteIp))
    {
        await throttle;
        return Results.Unauthorized();
    }
    if (!SecretMatches(request.Password, password))
    {
        limiter.RecordFailure(remoteIp);
        await throttle;
        return Results.Unauthorized();
    }

    var authorization = StyxToken.Mint(relayAuth.Keys[0], StyxToken.NewNetworkId()).ToString();
    await throttle;
    return Results.Ok(new NetworkConfigResponse(authorization));
});


if (app.Logger.IsEnabled(LogLevel.Information))
{
    app.Logger.LogInformation("Styx listening on port {Port}{LocalOnly}", port, localOnly ? " (localhost only)" : "");
    app.Logger.LogInformation("v2 tokens {V2}; v1 tokens {V1}; network creation {Creation}",
        auth.CanMint ? "accepted" : "refused (no RELAY_SIGNING_KEY or RELAY_SIGNING_PASSPHRASE)", auth.AcceptsV1 ? "accepted" : "refused (no RELAY_PASSWORD)",
        auth.CreatesNetworks ? "enabled" : "disabled (needs a v2 key and RELAY_PASSWORD)");
}
if (debugMessages) app.Logger.LogInformation("Message debug logging enabled");
app.Run();
return 0;

static string RemoteIpOf(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? ClientRegistry.Unknown;

// digests, so the comparison takes the same time whatever either length is
static bool SecretMatches(string? given, string expected) =>
    given is not null && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(given)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

internal record StatusResponse(string[] Peers);

internal record NetworkConfigRequest(string? Password);
internal record NetworkConfigResponse(string Authorization);

// exposes Program for WebApplicationFactory in tests
namespace Styx
{
    public class Program;
}
