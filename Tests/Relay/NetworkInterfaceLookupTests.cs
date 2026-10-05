using System.Net;
using System.Net.NetworkInformation;
using Hydra.Relay;

namespace Tests.Relay;

[TestFixture]
public class NetworkInterfaceLookupTests
{
    [Test]
    public void FindByAddress_FindsTheLoopbackInterface_InEitherForm()
    {
        var plain = NetworkInterfaceLookup.FindByAddress(IPAddress.Loopback);
        var mapped = NetworkInterfaceLookup.FindByAddress(IPAddress.Loopback.MapToIPv6());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plain?.NetworkInterfaceType, Is.EqualTo(NetworkInterfaceType.Loopback));
            Assert.That(mapped?.Name, Is.EqualTo(plain?.Name));
        }
    }

    [Test]
    public void FindByAddress_ReturnsNull_ForAnAddressNoInterfaceOwns() =>
        Assert.That(NetworkInterfaceLookup.FindByAddress(IPAddress.Parse("192.0.2.123")), Is.Null);

    [TestCase("utun4", NetworkInterfaceType.Unknown, "VPN / tunnel")]
    [TestCase("tun0", NetworkInterfaceType.Unknown, "VPN / tunnel")]
    [TestCase("tap1", NetworkInterfaceType.Unknown, "VPN / tunnel")]
    [TestCase("gif0", NetworkInterfaceType.Unknown, "Unknown")]
    [TestCase("utun4", NetworkInterfaceType.Ethernet, "Ethernet")]
    [TestCase("wg0", NetworkInterfaceType.Tunnel, "VPN / tunnel")]
    [TestCase("en0", NetworkInterfaceType.Wireless80211, "Wi-Fi")]
    [TestCase("en7", NetworkInterfaceType.GigabitEthernet, "Ethernet")]
    [TestCase("lo0", NetworkInterfaceType.Loopback, "loopback")]
    [TestCase("ppp0", NetworkInterfaceType.Ppp, "PPP")]
    public void Describe_NamesTheInterfaceKind(string name, NetworkInterfaceType type, string expected) =>
        Assert.That(NetworkInterfaceLookup.Describe(new StubInterface(name, type)), Is.EqualTo(expected));

    [Test]
    public void Describe_NoInterface_IsUnknown() =>
        Assert.That(NetworkInterfaceLookup.Describe(null), Is.EqualTo("unknown"));

    private sealed class StubInterface(string name, NetworkInterfaceType type) : NetworkInterface
    {
        public override string Name => name;
        public override NetworkInterfaceType NetworkInterfaceType => type;
    }
}
