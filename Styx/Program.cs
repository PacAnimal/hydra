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

if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RELAY_PASSWORD")))
{
    Console.Error.WriteLine("RELAY_PASSWORD environment variable is not set — refusing to start");
    return 1;
}

var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;

builder.DisableEventLog();
services.AddSereneConsoleLogging();

services.ConfigureHttpJsonOptions(options => SaneJson.Configure(options.SerializerOptions));
services.AddDataProtection().PersistKeysToNowhere();

services.AddSignalR(options =>
{
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
    options.EnableDetailedErrors = true;
    options.MaximumReceiveMessageSize = (long)ByteSize.FromMebiBytes(32).Bytes;
    options.MaximumParallelInvocationsPerClient = 4;
}).AddJsonProtocol(hubOptions =>
{
    SaneJson.Configure(hubOptions.PayloadSerializerOptions);
    hubOptions.PayloadSerializerOptions.WriteIndented = false;
});

services.AddSingleton<IClientRegistry, ClientRegistry>();
services.AddHostedService<IPeerBroadcaster, PeerBroadcastService>();
services.AddSingleton<AuthenticationHubFilter>();
services.Configure<HubOptions>(options => options.AddFilter<AuthenticationHubFilter>());

services.AddCathedralForwardedHeaders();

var port = int.Parse(config.GetString("LOCAL_PORT", "5000"));
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.IPv6Any, port, listenOptions =>
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

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHub<StyxHub>("/relay");

app.MapPost("/api/network-config", async (NetworkConfigRequest request) =>
{
    var throttle = Task.Delay(TimeSpan.FromSeconds(5));

    var password = Environment.GetEnvironmentVariable("RELAY_PASSWORD");
    if (string.IsNullOrEmpty(password) || request.Password != password)
    {
        await throttle;
        return Results.Unauthorized();
    }

    var networkId = Guid.NewGuid();
    var authorization = await new SimpleAes(password).EncryptBase64(networkId, CancellationToken.None);
    await throttle;
    return Results.Ok(new NetworkConfigResponse(authorization));
});


app.Logger.LogInformation("Styx listening on port {Port}", port);
app.Run();
return 0;

internal record NetworkConfigRequest(string Password);
internal record NetworkConfigResponse(string Authorization);

// exposes Program for WebApplicationFactory in tests
namespace Styx
{
    public class Program;
}
