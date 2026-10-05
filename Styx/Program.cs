using Cathedral.Config;
using Cathedral.Extensions;
using Cathedral.Logging;
using Cathedral.Utils;
using Styx;
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

services.AddSereneConsoleLogging(logging =>
{
    logging.TimestampFormat = "yyyy-MM-dd HH:mm:ss";
    logging.TimestampUtc = true;
    logging.FilterMicrosoftSpam = true;
});
services.ConfigureHttpJsonOptions(options => SaneJson.Configure(options.SerializerOptions));

var debugMessages = Environment.GetEnvironmentVariable(Constants.DebugMessagesEnvVar)?.EqualsIgnoreCase("true") ?? false;
builder.AddStyxRelay(new EnvironmentStyxPasswordProvider(), new StyxOptions(debugMessages));

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

app.MapGet("/api/status", async (HttpContext http, IStyxPasswordProvider passwordProvider, IClientRegistry registry, ResponseThrottle throttler,
    CancellationToken ct) =>
{
    var throttle = throttler.Start(Constants.StatusThrottleSeconds, ct);

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

app.MapPost("/api/network-config", async (NetworkConfigRequest request, IStyxPasswordProvider passwordProvider, ResponseThrottle throttler,
    CancellationToken ct) =>
{
    var throttle = throttler.Start(Constants.NetworkConfigThrottleSeconds, ct);

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


if (app.Logger.IsEnabled(LogLevel.Information))
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
