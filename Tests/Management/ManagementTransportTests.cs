using System.IO.Pipes;
using System.Net.Sockets;
using Hydra.Management;
using Tests.Setup;

namespace Tests.Management;

public class ManagementTransportTests
{
    [Test]
    public async Task Client_ExchangesBoundedJsonMessageOverLocalTransport()
    {
        var configPath = Path.Combine(TestPaths.FreshFixtureRoot(nameof(ManagementTransportTests)), "transport.conf");
        var endpoint = ManagementEndpoint.ForConfig(configPath);
        using var timeout = new CancellationTokenSource(Bound);

        Task server;
        Socket? listener = null;
        if (endpoint.IsNamedPipe)
        {
            server = ServePipe(endpoint.Address, endpoint.InstanceId, timeout.Token);
        }
        else
        {
            if (File.Exists(endpoint.Address)) File.Delete(endpoint.Address);
            listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(endpoint.Address));
            listener.Listen(1);
            server = ServeSocket(listener, endpoint.InstanceId, timeout.Token);
        }

        try
        {
            var hello = await new ManagementClient(configPath).Hello(timeout.Token);
            await server;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(hello.ProtocolVersion, Is.EqualTo(ManagementProtocol.Version));
                Assert.That(hello.InstanceId, Is.EqualTo(endpoint.InstanceId));
                Assert.That(hello.ProcessId, Is.EqualTo(42));
            }
        }
        finally
        {
            listener?.Dispose();
            if (!endpoint.IsNamedPipe && File.Exists(endpoint.Address)) File.Delete(endpoint.Address);
        }
    }

    [Test]
    public async Task Client_SendsShutdownCommandOverLocalTransport()
    {
        var configPath = Path.Combine(TestPaths.FreshFixtureRoot(nameof(ManagementTransportTests)), "shutdown.conf");
        var endpoint = ManagementEndpoint.ForConfig(configPath);
        using var timeout = new CancellationTokenSource(Bound);

        Task server;
        Socket? listener = null;
        if (endpoint.IsNamedPipe)
        {
            server = ServePipe(endpoint.Address, endpoint.InstanceId, timeout.Token, expectShutdown: true);
        }
        else
        {
            if (File.Exists(endpoint.Address)) File.Delete(endpoint.Address);
            listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(endpoint.Address));
            listener.Listen(1);
            server = ServeSocket(listener, endpoint.InstanceId, timeout.Token, expectShutdown: true);
        }

        try
        {
            var result = await new ManagementClient(configPath).ShutdownHydra(timeout.Token);
            await server;
            Assert.That(result, Is.EqualTo(new CommandResult(true, "Shutdown requested.")));
        }
        finally
        {
            listener?.Dispose();
            if (!endpoint.IsNamedPipe && File.Exists(endpoint.Address)) File.Delete(endpoint.Address);
        }
    }

    private static async Task ServePipe(string name, string instanceId, CancellationToken cancel, bool expectShutdown = false)
    {
        await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.WaitForConnectionAsync(cancel);
        await Respond(pipe, instanceId, expectShutdown, cancel);
    }

    private static async Task ServeSocket(Socket listener, string instanceId, CancellationToken cancel, bool expectShutdown = false)
    {
        using var socket = await listener.AcceptAsync(cancel);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        await Respond(stream, instanceId, expectShutdown, cancel);
    }

    private static async Task Respond(Stream stream, string instanceId, bool expectShutdown, CancellationToken cancel)
    {
        var request = await ManagementFraming.Read<ManagementRequest>(stream, cancel);
        if (expectShutdown)
        {
            Assert.That(request.Method, Is.EqualTo(ManagementMethods.HydraShutdown));
            await ManagementFraming.Write(stream,
                ManagementResponse.Ok(new CommandResult(true, "Shutdown requested.")), cancel);
            return;
        }
        Assert.That(request.Method, Is.EqualTo(ManagementMethods.Hello));
        await ManagementFraming.Write(stream,
            ManagementResponse.Ok(new ServerHello(ManagementProtocol.Version, "1.2.3", instanceId, 42)), cancel);
    }
}
