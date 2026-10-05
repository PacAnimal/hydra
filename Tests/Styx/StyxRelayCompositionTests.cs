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
        Assert.That(services.GetRequiredService<IStyxPasswordProvider>(), Is.InstanceOf<EnvironmentStyxPasswordProvider>());
        Assert.That(services.GetRequiredService<IStyxPasswordProvider>().Password, Is.EqualTo(StyxTestServer.TestPassword));
    }

    [Test]
    public async Task TheEmbeddedRelayComposesTheRelayServices()
    {
        var config = new EmbeddedStyxServerConfig { Port = 0, Password = EmbeddedPassword };
        await using var app = new EmbeddedStyxServer(config, NullLogger<EmbeddedStyxServer>.Instance).BuildApp();
        var services = app.Services;

        AssertRelayServices(services);
        Assert.That(services.GetRequiredService<IStyxPasswordProvider>(), Is.InstanceOf<InlineStyxPasswordProvider>());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(services.GetRequiredService<IStyxPasswordProvider>().Password, Is.EqualTo(EmbeddedPassword));
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

        Assert.That(await observed.Task.WaitAsync(TimeSpan.FromSeconds(30)), Is.True);
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
            Assert.That(services.GetRequiredService<StyxOptions>(), Is.Not.Null);
        }
    }
}
