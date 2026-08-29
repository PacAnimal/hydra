using Hydra.Management;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net.Sockets;

namespace Tests.Management;

public class ManagementServerTests
{
    [TestCase(true, "Hydra shutdown requested.")]
    [TestCase(false, "Shutdown is unavailable.")]
    public async Task ShutdownDispatchReturnsTheLifetimeDecision(bool accepted, string message)
    {
        var lifetime = new FakeLifetime(new CommandResult(accepted, message));
        var server = new ManagementServer(
            new HydraRuntimeInfo(Path.Combine(TestContext.CurrentContext.WorkDirectory,
                $"server-{Guid.NewGuid():N}.conf"), DateTimeOffset.UtcNow),
            null!, null!, null!, lifetime, null!, NullLogger<ManagementServer>.Instance);

        var response = await server.DispatchAsync(new ManagementRequest("hydra.shutdown"), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(response.Success, Is.True);
            Assert.That(ManagementJson.Deserialize<CommandResult>(response.Json),
                Is.EqualTo(new CommandResult(accepted, message)));
            Assert.That(lifetime.ShutdownRequests, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task UnixServer_BoundsConcurrentStalledHandlers()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("Unix-socket concurrency test");
        var configPath = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            $"server-{Guid.NewGuid():N}.conf");
        var endpoint = ManagementEndpoint.ForConfig(configPath);
        var server = new ManagementServer(
            new HydraRuntimeInfo(configPath, DateTimeOffset.UtcNow),
            null!, null!, null!, new FakeLifetime(new CommandResult(true, "ok")), null!,
            NullLogger<ManagementServer>.Instance);
        var clients = new List<Socket>();

        await server.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (!File.Exists(endpoint.Address) && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            Assert.That(File.Exists(endpoint.Address), Is.True, "management socket did not become ready");

            for (var i = 0; i < ManagementServer.MaxConcurrentHandlers + 4; i++)
            {
                var socket = await ConnectUnixAsync(endpoint.Address);
                clients.Add(socket); // leave the frame incomplete so each accepted handler remains active
            }

            deadline = DateTime.UtcNow.AddSeconds(2);
            while (server.ActiveHandlerCount < ManagementServer.MaxConcurrentHandlers && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            Assert.That(server.ActiveHandlerCount, Is.EqualTo(ManagementServer.MaxConcurrentHandlers));
        }
        finally
        {
            foreach (var client in clients) client.Dispose();
            await server.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<Socket> ConnectUnixAsync(string address)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (true)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(address));
                return socket;
            }
            catch (SocketException) when (DateTime.UtcNow < deadline)
            {
                socket.Dispose();
                await Task.Delay(10);
            }
        }
    }

    private sealed class FakeLifetime(CommandResult shutdownResult) : IHydraLifetimeController
    {
        internal int ShutdownRequests { get; private set; }

        public void RestartAfterResponse() { }

        public CommandResult ShutdownAfterResponse()
        {
            ShutdownRequests++;
            return shutdownResult;
        }
    }
}
