using System.Net;
using System.Net.Sockets;
using Hydra.Relay;

namespace Tests.Relay;

[TestFixture]
public class RelaySocketConnectorTests
{
    [Test]
    public async Task ConnectAsync_FirstAddressFails_FallsBackToNextAddress()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepting = listener.AcceptSocketAsync();

        using var connected = await RelaySocketConnector.ConnectAsync(
            [IPAddress.IPv6Loopback, IPAddress.Loopback],
            port,
            TimeSpan.FromMilliseconds(250),
            CancellationToken.None);
        using var accepted = await accepting.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.That(connected.Connected, Is.True);
        Assert.That(((IPEndPoint)connected.RemoteEndPoint!).Address, Is.EqualTo(IPAddress.Loopback));
    }

    [Test]
    public void ConnectAsync_NoResolvedAddresses_ReportsHostNotFound()
    {
        var error = Assert.ThrowsAsync<SocketException>(async () =>
            await RelaySocketConnector.ConnectAsync(
                [],
                51600,
                TimeSpan.FromMilliseconds(50),
                CancellationToken.None));

        Assert.That(error!.SocketErrorCode, Is.EqualTo(SocketError.HostNotFound));
    }
}
