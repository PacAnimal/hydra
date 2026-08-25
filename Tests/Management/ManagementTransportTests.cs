using System.IO.Pipes;
using System.Net.Sockets;
using Hydra.Management;

namespace Tests.Management;

public class ManagementTransportTests
{
    [Test]
    public async Task Client_ExchangesBoundedJsonMessageOverLocalTransport()
    {
        var configPath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"transport-{Guid.NewGuid():N}.conf");
        var endpoint = ManagementEndpoint.ForConfig(configPath);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        Task server;
        Socket? listener = null;
        if (endpoint.IsNamedPipe)
        {
            server = ServePipeAsync(endpoint.Address, endpoint.InstanceId, timeout.Token);
        }
        else
        {
            if (File.Exists(endpoint.Address)) File.Delete(endpoint.Address);
            listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(endpoint.Address));
            listener.Listen(1);
            server = ServeSocketAsync(listener, endpoint.InstanceId, timeout.Token);
        }

        try
        {
            var hello = await new ManagementClient(configPath).HelloAsync(timeout.Token);
            await server;
            Assert.Multiple(() =>
            {
                Assert.That(hello.ProtocolVersion, Is.EqualTo(ManagementProtocol.Version));
                Assert.That(hello.InstanceId, Is.EqualTo(endpoint.InstanceId));
                Assert.That(hello.ProcessId, Is.EqualTo(42));
            });
        }
        finally
        {
            listener?.Dispose();
            if (!endpoint.IsNamedPipe && File.Exists(endpoint.Address)) File.Delete(endpoint.Address);
        }
    }

    private static async Task ServePipeAsync(string name, string instanceId, CancellationToken cancel)
    {
        await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.WaitForConnectionAsync(cancel);
        await RespondAsync(pipe, instanceId, cancel);
    }

    private static async Task ServeSocketAsync(Socket listener, string instanceId, CancellationToken cancel)
    {
        using var socket = await listener.AcceptAsync(cancel);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        await RespondAsync(stream, instanceId, cancel);
    }

    private static async Task RespondAsync(Stream stream, string instanceId, CancellationToken cancel)
    {
        var request = await ManagementFraming.ReadAsync<ManagementRequest>(stream, cancel);
        Assert.That(request.Method, Is.EqualTo("hello"));
        await ManagementFraming.WriteAsync(stream,
            ManagementResponse.Ok(new ServerHello(ManagementProtocol.Version, "1.2.3", instanceId, 42)), cancel);
    }
}
