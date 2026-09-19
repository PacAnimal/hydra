using Hydra.Management;

namespace Tests.Management;

public class MockManagementClientTests
{
    [Test]
    public async Task StatusReportsAConnectedRelayAndAConnectedPeer()
    {
        var client = new MockManagementClient();

        var status = await client.GetStatusAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(status.RelayConnected, Is.True);
            Assert.That(status.RelayConnection, Is.Not.Null);
            Assert.That(status.Peers, Has.Count.EqualTo(1));
            Assert.That(status.Peers[0].Connected, Is.True);
            Assert.That(status.UptimeSeconds, Is.GreaterThan(0));
        }
    }

    [Test]
    public async Task StatusUsesOnlyReservedDocumentationAddresses()
    {
        // Everything this backs (design-preview mode, screenshots) can end up published, so
        // nothing it reports may look like it could route anywhere real — RFC 5737 exists
        // exactly for this: 192.0.2.0/24, 198.51.100.0/24, 203.0.113.0/24, and the 2001:db8::/32
        // IPv6 equivalent are permanently reserved and guaranteed never publicly routable.
        var client = new MockManagementClient();

        var status = await client.GetStatusAsync();

        var addresses = new List<string> { status.RelayConnection!.LocalAddress, status.RelayConnection.RemoteAddress };
        addresses.AddRange(status.ActiveNetworkAdapters!.SelectMany(adapter => adapter.Addresses));
        addresses.AddRange(status.EmbeddedRelayPeers!.SelectMany(peer => new[] { peer.LocalAddress, peer.RemoteAddress }));

        Assert.That(addresses, Has.All.Matches<string>(address =>
            address.StartsWith("192.0.2.") || address.StartsWith("198.51.100.") || address.StartsWith("203.0.113.")
            || address.StartsWith("2001:db8:") || address is "127.0.0.1"));
    }

    [Test]
    public async Task ConnectedForGrowsWithUptimeInsteadOfLookingLikeAFreshProcess()
    {
        var client = new MockManagementClient();

        var status = await client.GetStatusAsync();

        var connectedFor = status.CapturedAt - status.RelayConnection!.ConnectedAt;
        Assert.That(connectedFor.TotalMinutes, Is.GreaterThan(1),
            "a demo meant to look like an established, busy session shouldn't show a connection that only just started");
    }

    [Test]
    public async Task MutatingCommandsReportAcceptedWithoutThrowing()
    {
        var client = new MockManagementClient();

        using (Assert.EnterMultipleScope())
        {
            Assert.That((await client.ReconnectRelayAsync()).Accepted, Is.True);
            Assert.That((await client.RestartHydraAsync()).Accepted, Is.True);
            Assert.That((await client.ShutdownHydraAsync()).Accepted, Is.True);
        }
    }

    [Test]
    public async Task ConfigRoundTripsThroughGetAndSave()
    {
        var client = new MockManagementClient();

        var config = await client.GetConfigAsync();
        var saved = await client.SaveConfigAsync(new SaveConfigRequest(config.Revision, config.Json, Restart: false));

        Assert.That(saved.Json, Is.EqualTo(config.Json));
    }
}
