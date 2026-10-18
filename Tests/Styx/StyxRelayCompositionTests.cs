using Hydra.Config;
using Hydra.Relay;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Styx;
using Styx.Filters;
using Styx.Services;
using System.Net;
using System.Net.Sockets;
using Tests.Setup;

namespace Tests.Styx;

/// <summary>
/// Both relay hosts compose the same services through <c>AddStyxRelay</c>, and differ only where they mean to.
/// </summary>
[TestFixture]
public class StyxRelayCompositionTests
{
    private const string EmbeddedPassword = "embedded-composition-password";

    [Test]
    public async Task TheStandaloneRelayComposesTheRelayServices()
    {
        await using var factory = StyxTestServer.Create();
        var services = factory.Services;

        AssertRelayServices(services);
        var auth = services.GetRequiredService<StyxAuth>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(auth.V1Password, Is.EqualTo(StyxTestServer.TestPassword));
            Assert.That(auth.Keys, Has.Count.EqualTo(1));
            Assert.That(auth.CanMint, Is.True);
        }
    }

    [Test]
    public async Task TheEmbeddedRelayComposesTheRelayServices()
    {
        var config = new EmbeddedStyxServerConfig { Port = 0, Password = EmbeddedPassword };
        using var embedded = new EmbeddedStyxServer(config, NullLogger<EmbeddedStyxServer>.Instance);
        await using var app = embedded.BuildApp();
        var services = app.Services;

        AssertRelayServices(services);
        var auth = services.GetRequiredService<StyxAuth>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(auth.V1Password, Is.EqualTo(EmbeddedPassword));
            Assert.That(auth.CanMint, Is.False, "the embedded relay stays v1 only");
            Assert.That(services.GetRequiredService<StyxOptions>().DebugMessages, Is.False);
        }
    }

    // the transport default is overridden to off, so only the extension can turn it back on
    [Test]
    public async Task UseTcpNoDelaySetsNoDelayOnAcceptedSockets()
    {
        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseSockets(o => o.NoDelay = false);
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listenOptions =>
        {
            listenOptions.UseTcpNoDelay();
            listenOptions.Use(next => ctx =>
            {
                var socket = ctx.Features.Get<IConnectionSocketFeature>()?.Socket;
                if (socket == null) observed.TrySetException(new InvalidOperationException("connection has no socket"));
                else observed.TrySetResult(socket.NoDelay);
                return next(ctx);
            });
        }));
        await using var app = builder.Build();
        await app.StartAsync();

        var address = new Uri(app.Urls.Single());
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, address.Port);

        Assert.That(await observed.Task.WaitAsync(Bound), Is.True);
        await app.StopAsync();
    }

    private static void AssertRelayServices(IServiceProvider services)
    {
        var broadcaster = services.GetRequiredService<IPeerBroadcaster>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(services.GetRequiredService<IClientRegistry>(), Is.InstanceOf<ClientRegistry>());
            Assert.That(broadcaster, Is.InstanceOf<PeerBroadcastService>());
            Assert.That(services.GetServices<IHostedService>(), Does.Contain(broadcaster), "the broadcaster's queue is drained only when it runs as a hosted service");
            Assert.That(services.GetRequiredService<AuthenticationHubFilter>(), Is.Not.Null);
            Assert.That(services.GetRequiredService<ITokenVerifier>(), Is.InstanceOf<TokenVerifier>());
            Assert.That(services.GetRequiredService<IExpiryScheduler>(), Is.InstanceOf<ExpiryScheduler>());
            Assert.That(services.GetRequiredService<StyxOptions>(), Is.Not.Null);
        }
    }
}
