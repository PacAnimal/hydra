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
    public async Task StatusUsesOnlyPrivateOrReservedAddressesNeverARealPublicOne()
    {
        // Everything this backs (design-preview mode, screenshots) can end up published, so
        // nothing it reports may look like it could identify or route to a real host. Local-network
        // fields (this machine's own adapters, the embedded-relay peer) use ordinary RFC 1918 /
        // RFC 4193 private-use space — safe because millions of real LANs share it, so it can never
        // point at one specific network. The relay's own address is the one field representing a
        // public, internet-facing endpoint, so it alone must stay in RFC 5737's reserved
        // documentation range (guaranteed to never be a real, reachable host).
        var client = new MockManagementClient();

        var status = await client.GetStatusAsync();

        var localAddresses = new List<string> { status.RelayConnection!.LocalAddress };
        localAddresses.AddRange(status.ActiveNetworkAdapters!.SelectMany(adapter => adapter.Addresses));
        localAddresses.AddRange(status.EmbeddedRelayPeers!.SelectMany(peer => new[] { peer.LocalAddress, peer.RemoteAddress }));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(localAddresses, Has.All.Matches<string>(IsPrivateOrLoopback));
            Assert.That(status.RelayConnection.RemoteAddress, Does.StartWith("192.0.2."),
                "the relay's own address represents a public endpoint and must stay non-routable documentation space");
        }
    }

    private static bool IsPrivateOrLoopback(string address) =>
        address.StartsWith("192.168.") || address.StartsWith("10.") || address is "127.0.0.1"
        || address.StartsWith("fd") || address.StartsWith("fc")
        || Enumerable.Range(16, 16).Any(n => address.StartsWith($"172.{n}."));

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
