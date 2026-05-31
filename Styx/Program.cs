using ByteSizeLib;
using Cathedral.Config;
using Cathedral.Extensions;
using Cathedral.Logging;
using Cathedral.Utils;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.SignalR;
using Styx;
using Styx.Filters;
using Styx.Services;
using System.Net;

var config = Env.Config;

if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Constants.RelayPasswordEnvVar)))
{
    Console.Error.WriteLine("RELAY_PASSWORD environment variable is not set — refusing to start");
    return 1;
}

var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;

builder.DisableEventLog();
services.AddSereneConsoleLogging(logging =>
{
    logging.TimestampFormat = "yyyy-MM-dd HH:mm:ss";
    logging.TimestampUtc = true;
    logging.FilterMicrosoftSpam = true;
});
services.ConfigureSereneHttpLogging(opts =>
{
    opts.ServerRequestStartingLogLevel = LogLevel.Debug;
});

services.ConfigureHttpJsonOptions(options => SaneJson.Configure(options.SerializerOptions));
services.AddDataProtection().PersistKeysToNowhere();

services.AddSignalR(options =>
{
    options.KeepAliveInterval = TimeSpan.FromSeconds(Constants.KeepAliveSeconds);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(Constants.ClientTimeoutSeconds);
    options.EnableDetailedErrors = true;
    options.MaximumReceiveMessageSize = (long)ByteSize.FromMebiBytes(Constants.MaxMessageMebiBytes).Bytes;
    options.MaximumParallelInvocationsPerClient = Constants.MaxParallelInvocations;
}).AddMessagePackProtocol();

var debugMessages = Environment.GetEnvironmentVariable(Constants.DebugMessagesEnvVar)?.EqualsIgnoreCase("true") ?? false;
services.AddSingleton(new StyxOptions(debugMessages));

services.AddSingleton<IClientRegistry, ClientRegistry>();
services.AddHostedService<IPeerBroadcaster, PeerBroadcastService>();
services.AddSingleton<IStyxPasswordProvider, EnvironmentStyxPasswordProvider>();
services.AddSingleton<AuthenticationHubFilter>();
services.Configure<HubOptions>(options => options.AddFilter<AuthenticationHubFilter>());

services.AddCathedralForwardedHeaders();

var port = int.Parse(config.GetString("LOCAL_PORT", "5000"));
var localOnly = Environment.GetEnvironmentVariable(Constants.LocalOnlyEnvVar)?.EqualsIgnoreCase("true") ?? false;
builder.WebHost.ConfigureKestrel(options =>
{
    void ConfigureListener(IPAddress address) => options.Listen(address, port, listenOptions =>
    {
        listenOptions.Use(next => ctx =>
        {
            var socketFeature = ctx.Features.Get<IConnectionSocketFeature>();
            if (socketFeature != null) socketFeature.Socket.NoDelay = true;
            return next(ctx);
        });
    });

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

app.MapGet("/api/status", async (HttpContext http, IStyxPasswordProvider passwordProvider, IClientRegistry registry, CancellationToken ct) =>
{
    var throttle = Task.Delay(TimeSpan.FromSeconds(Constants.StatusThrottleSeconds), ct);

    var bearer = http.Request.Headers.Authorization.ToString();
    var token = bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? bearer["Bearer ".Length..] : null;

    Guid networkId;
    try
    {
        var password = passwordProvider.Password;
        networkId = await new SimpleAes(password).DecryptBase64<Guid>(token!, true, ct);
    }
    catch
    {
        await throttle;
        return Results.Unauthorized();
    }

    var clients = await registry.GetNetworkClients(networkId);
    return Results.Ok(new StatusResponse([.. clients.Select(c => c.HostName)]));
});

app.MapPost("/api/network-config", async (NetworkConfigRequest request, IStyxPasswordProvider passwordProvider, CancellationToken ct) =>
{
    var throttle = Task.Delay(TimeSpan.FromSeconds(Constants.NetworkConfigThrottleSeconds), ct);

    string password;
    try { password = passwordProvider.Password; }
    catch { await throttle; return Results.Unauthorized(); }

    if (request.Password != password)
    {
        await throttle;
        return Results.Unauthorized();
    }

    var networkId = Guid.NewGuid();
    var authorization = await new SimpleAes(password).EncryptBase64(networkId, CancellationToken.None);
    await throttle;
    return Results.Ok(new NetworkConfigResponse(authorization));
});


app.Logger.LogInformation("Styx listening on port {Port}{LocalOnly}", port, localOnly ? " (localhost only)" : "");
if (debugMessages) app.Logger.LogInformation("Message debug logging enabled");
app.Run();
return 0;

internal record StatusResponse(string[] Peers);

internal record NetworkConfigRequest(string Password);
internal record NetworkConfigResponse(string Authorization);

// exposes Program for WebApplicationFactory in tests
namespace Styx
{
    public class Program;
}
