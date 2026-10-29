using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using Hydra.Platform;
using Hydra.Platform.Linux;
using Hydra.Platform.MacOs;
using Tests.Setup;

namespace Tests.Platform;

[TestFixture]
public class NetworkLinksTests
{
    private static readonly PhysicalAddress Router = PhysicalAddress.Parse("6C-7F-0C-B7-48-29");

    [Test]
    public void FormatMac_IsLowercaseAndColonSeparated()
    {
        Assert.That(NetworkLinks.FormatMac(Router), Is.EqualTo("6c:7f:0c:b7:48:29"));
    }

    [Test]
    public void Describe_LeavesOutLinkLocalAddresses()
    {
        var links = new NetworkLinks([IPAddress.Parse("fe80::1"), IPAddress.Parse("169.254.3.4"), IPAddress.Parse("10.143.50.63")], [Router], Wired: true);
        Assert.That(links.Describe(), Is.EqualTo("wired, addresses 10.143.50.63, gateway MAC 6c:7f:0c:b7:48:29"));
    }

    [Test]
    public void Describe_SaysNoneWhenThereIsNothing()
    {
        Assert.That(NetworkLinks.None.Describe(), Is.EqualTo("not wired, addresses none, gateway MAC none"));
    }

    [Test]
    public void Describe_SaysWhenTheGatewayIsUnknown()
    {
        Assert.That(new NetworkLinks([], null, Wired: false).Describe(), Is.EqualTo("not wired, addresses none, gateway MAC unknown"));
    }

    [Test]
    public void LinuxArp_KeepsOnlyResolvedEntries()
    {
        const string table = """
            IP address       HW type     Flags       HW address            Mask     Device
            192.168.1.1      0x1         0x2         6c:7f:0c:b7:48:29     *        eth0
            192.168.1.9      0x1         0x0         00:00:00:00:00:00     *        eth0
            10.0.0.1         0x1         0x6         a4:91:b1:0c:22:7e     *        wlan0

            """;

        var neighbours = LinuxNetworkLinks.ParseArp(table);

        Assert.That(neighbours, Is.EqualTo(
        [
            new Neighbour(IPAddress.Parse("192.168.1.1"), Router),
            new Neighbour(IPAddress.Parse("10.0.0.1"), PhysicalAddress.Parse("A4-91-B1-0C-22-7E"))
        ]));
    }

    [Test]
    public void LinuxSysfs_WiredIsAPhysicalEthernetDeviceThatIsNotWireless()
    {
        var root = TestPaths.FreshFixtureRoot(nameof(NetworkLinksTests));
        var nic = Device(root, "devices/pci0000_00/0000_00_1f.6/net/eth0");
        var wifi = Device(root, "devices/pci0000_00/0000_02_00.0/net/wlan0");
        Directory.CreateDirectory(Path.Combine(wifi, "phy80211"));
        var bridge = Device(root, "devices/virtual/net/docker0");
        var infiniband = Device(root, "devices/pci0000_00/0000_03_00.0/net/ib0", type: "32");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(LinuxNetworkLinks.IsPhysicalWired(nic), Is.True);
            Assert.That(LinuxNetworkLinks.IsPhysicalWired(wifi), Is.False);
            Assert.That(LinuxNetworkLinks.IsPhysicalWired(bridge), Is.False);
            Assert.That(LinuxNetworkLinks.IsPhysicalWired(infiniband), Is.False);
        }
    }

    [Test]
    public void ShieldNeighbours_KeepOnlyWholeEntries()
    {
        var reply = new ShieldMessage("ack", new Dictionary<string, object?>
        {
            ["seq"] = 7L,
            ["entries"] = new object?[]
            {
                new Dictionary<string, object?> { ["ip"] = "192.168.1.1", ["mac"] = Router.GetAddressBytes() },
                new Dictionary<string, object?> { ["ip"] = "192.168.1.9", ["mac"] = new byte[] { 1, 2, 3 } },
                new Dictionary<string, object?> { ["ip"] = "not an ip", ["mac"] = Router.GetAddressBytes() },
                "not an entry"
            }
        });

        Assert.That(MacShieldProcess.ParseNeighbours(reply), Is.EqualTo([new Neighbour(IPAddress.Parse("192.168.1.1"), Router)]));
    }

    private static string Device(string root, string path, string type = "1")
    {
        var dir = Path.Combine(root, path);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "type"), $"{type}\n");
        return dir;
    }
}

/// <summary>The reader over this machine's real interface list, with a neighbour table the test controls.</summary>
[TestFixture]
public class NetworkLinkReaderTests
{
    private static readonly PhysicalAddress Gateway = PhysicalAddress.Parse("02-00-00-00-00-01");
    private static readonly Neighbour Bystander = new(IPAddress.Parse("198.51.100.77"), PhysicalAddress.Parse("02-00-00-00-00-77"));

    // every address in the table resolved to Gateway, unless empty
    private sealed class FixedNeighbours(bool empty) : INetworkLinkSource
    {
        public int Resolves;

        public bool HasWiredLink(IReadOnlyList<NetworkInterface> live) => false;

        public async ValueTask<IReadOnlyList<Neighbour>> Neighbours(IReadOnlyList<IPAddress> gateways, CancellationToken cancel)
        {
            await ValueTask.CompletedTask;
            return empty ? [] : [Bystander, .. gateways.Select(g => new Neighbour(g, Gateway))];
        }

        public void Resolve(IReadOnlyList<IPAddress> gateways) => Resolves++;
    }

    // only a gateway's entry counts: a neighbour that routes nothing names no network
    [Test]
    public async Task Read_TakesGatewayMacsFromTheNeighbourTableOnly()
    {
        var source = new FixedNeighbours(empty: false);

        var links = await NetworkLinkReader.Read(source, gatewayMacs: true, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(links.GatewayMacs, Is.Not.Null);
            Assert.That(links.GatewayMacs, Is.SubsetOf([Gateway]));
            Assert.That(source.Resolves, Is.Zero);
        }
    }

    // with no gateway the answer is none; with one that never resolves it is unknown, after asking the OS to resolve it
    [Test]
    public async Task Read_AnUnresolvedGatewayIsUnknown()
    {
        var source = new FixedNeighbours(empty: true);

        var links = await NetworkLinkReader.Read(source, gatewayMacs: true, CancellationToken.None);

        // Resolve is asked for exactly when there was a gateway to resolve
        if (source.Resolves == 0) Assert.That(links.GatewayMacs, Is.Empty);
        else Assert.That(links.GatewayMacs, Is.Null);
    }

    [Test]
    public async Task Read_WithoutGatewayMacs_LeavesTheTableAlone()
    {
        var links = await NetworkLinkReader.Read(new FixedNeighbours(empty: true), gatewayMacs: false, CancellationToken.None);
        Assert.That(links.GatewayMacs, Is.Null);
    }
}

[TestFixture]
[SupportedOSPlatform("linux")]
public class LinuxNetworkLinksTests
{
    [Test]
    public async Task Neighbours_ReadsTheKernelsTable()
    {
        var neighbours = await new LinuxNetworkLinks().Neighbours([], CancellationToken.None);
        Assert.That(neighbours.Select(n => n.Mac.GetAddressBytes().Length), Has.All.EqualTo(6));
    }
}
