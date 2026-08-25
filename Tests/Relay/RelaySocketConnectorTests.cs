using System.Net;
using System.Net.Sockets;
using Hydra.Relay;

namespace Tests.Relay;

[TestFixture]
public class RelaySocketConnectorTests
{
    [Test]
    public void OrderByInterfacePreference_UsesConfiguredOrder_AndPreservesTies()
    {
        var wifiFirst = IPAddress.Parse("192.168.1.21");
        var unknown = IPAddress.Parse("10.0.0.10");
        var ethernetFirst = IPAddress.Parse("192.168.1.129");
        var ethernetSecond = IPAddress.Parse("192.168.1.130");
        var interfaces = new Dictionary<IPAddress, string>
        {
            [wifiFirst] = "en0",
            [ethernetFirst] = "en7",
            [ethernetSecond] = "en7"
        };

        var ordered = RelayAddressPreference.OrderByInterfacePreference(
            [wifiFirst, unknown, ethernetFirst, ethernetSecond],
            address => interfaces.GetValueOrDefault(address),
            new Dictionary<string, int> { ["en7"] = 1, ["en0"] = 7 });

        Assert.That(ordered, Is.EqualTo(new[] { ethernetFirst, ethernetSecond, wifiFirst, unknown }));
    }

    [Test]
    public void ParseMacServiceOrder_MapsEnabledDevicesOnly()
    {
        const string output = """
            An asterisk (*) denotes that a network service is disabled.
            (1) USB Ethernet
            (Hardware Port: USB Ethernet, Device: en7)
            (*) Wi-Fi
            (Hardware Port: Wi-Fi, Device: en0)
            (3) Thunderbolt Bridge
            (Hardware Port: Thunderbolt Bridge, Device: bridge0)
            """;

        var preferences = RelayAddressPreference.ParseMacServiceOrder(output);

        Assert.That(preferences, Is.EqualTo(new Dictionary<string, int> { ["en7"] = 1, ["bridge0"] = 3 }));
    }

    [Test]
    public void ParseLinuxDefaultRoutes_UsesLowestMetric_ThenRouteOrder()
    {
        const string output = """
            default via 192.168.1.1 dev wlan0 proto dhcp metric 600
            default via 192.168.1.1 dev eth0 proto dhcp metric 100
            default via fe80::1 dev eth0 proto ra metric 1024
            """;

        var preferences = RelayAddressPreference.ParseLinuxDefaultRoutes(output);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(preferences["eth0"], Is.LessThan(preferences["wlan0"]));
            Assert.That(preferences, Has.Count.EqualTo(2));
        }
    }

    [Test]
    public void ParseWindowsInterfaceMetrics_UsesLowestMetricPerInterface()
    {
        const string output = """
            12|25
            7|5
            8|50
            invalid
            """;
        var names = new Dictionary<int, string> { [12] = "Wi-Fi", [7] = "Ethernet", [8] = "Ethernet" };

        var preferences = RelayAddressPreference.ParseWindowsInterfaceMetrics(
            output,
            index => names.GetValueOrDefault(index));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(preferences["Ethernet"], Is.EqualTo(5));
            Assert.That(preferences["Wi-Fi"], Is.EqualTo(25));
            Assert.That(preferences, Has.Count.EqualTo(2));
        }
    }

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
