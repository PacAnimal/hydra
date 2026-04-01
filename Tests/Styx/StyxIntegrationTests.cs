using Common.DTO;
using Hydra.Config;
using Hydra.Relay;
using Microsoft.AspNetCore.SignalR;
using Tests.Setup;

namespace Tests.Styx;

[TestFixture]
public class StyxIntegrationTests
{
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<global::Styx.Program>? _factory;

    [SetUp]
    public void SetUp() => _factory = StyxTestServer.Create();

    [TearDown]
    public async Task TearDown()
    {
        if (_factory != null)
            await _factory.DisposeAsync();
    }

    // ─── auth tests (TestStyxClient — controls the auth step directly) ───────

    [Test]
    public async Task Auth_WrongPassword_ReturnsUnauthenticated()
    {
        await using var client = new TestStyxClient();
        var auth = await StyxTestServer.GenerateAuthorization(Guid.NewGuid(), password: "wrong-password");
        var response = await client.Connect(_factory!, auth, "test-host");
        Assert.That(response.Authenticated, Is.False);
    }

    [Test]
    public async Task Auth_CorrectPassword_Authenticates()
    {
        await using var client = new TestStyxClient();
        var auth = await StyxTestServer.GenerateAuthorization(Guid.NewGuid());
        var response = await client.Connect(_factory!, auth, "test-host");
        Assert.That(response.Authenticated, Is.True);
    }

    [Test]
    public async Task Ping_WithoutAuth_ReturnsPong()
    {
        await using var client = new TestStyxClient();
        await client.ConnectRaw(_factory!);
        var result = await client.Server!.Ping();
        Assert.That(result, Is.True);
    }

    [Test]
    public async Task Send_WithoutAuth_FailsWithException()
    {
        // hub filter calls Context.Abort() on unauthenticated calls — client sees a cancellation or error
        await using var client = new TestStyxClient();
        await client.ConnectRaw(_factory!);
        Assert.CatchAsync(async () =>
            await client.Server!.Send(["any-host"], [1, 2, 3]));
    }

    [Test]
    public async Task Auth_MultipleNetworks_EachIsIndependent()
    {
        var networkA = Guid.NewGuid();
        var networkB = Guid.NewGuid();

        await using var clientA = new TestStyxClient();
        await using var clientB = new TestStyxClient();

        var authA = await StyxTestServer.GenerateAuthorization(networkA);
        var authB = await StyxTestServer.GenerateAuthorization(networkB);

        var respA = await clientA.Connect(_factory!, authA, "host");
        var respB = await clientB.Connect(_factory!, authB, "host"); // same hostname, different network

        using (Assert.EnterMultipleScope())
        {
            Assert.That(respA.Authenticated, Is.True);
            Assert.That(respB.Authenticated, Is.True);
        }
    }

    // ─── relay tests (HydraTestClient — real Hydra relay code + RelayEncryption) ─

    [Test]
    public async Task TwoHydraClients_MessageIntegrity()
    {
        var networkId = Guid.NewGuid();
        var key = StyxTestServer.GenerateEncryptionKey();
        var cfg = await StyxTestServer.BuildNetworkConfig(_factory!, networkId, key);

        await using var sender = new HydraTestClient(_factory!, new HydraConfig { Mode = Mode.Master, NetworkConfig = cfg, Name = "sender" });
        await using var receiver = new HydraTestClient(_factory!, new HydraConfig { Mode = Mode.Master, NetworkConfig = cfg, Name = "receiver" });

        await sender.StartAsync(CancellationToken.None);
        await receiver.StartAsync(CancellationToken.None);
        await sender.WaitForReady();
        await receiver.WaitForReady();

        var payload = MessageSerializer.Encode(MessageKind.MouseMove, new MouseMoveMessage(42, 99));
        await sender.Send(["receiver"], payload);

        var (source, kind, json) = await receiver.WaitForMessage();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(source, Is.EqualTo("sender"));
            Assert.That(kind, Is.EqualTo(MessageKind.MouseMove));
            Assert.That(json, Does.Contain("42"));
            Assert.That(json, Does.Contain("99"));
        }
    }

    [Test]
    public async Task TwoHydraClients_BidirectionalExchange()
    {
        var networkId = Guid.NewGuid();
        var cfg = await StyxTestServer.BuildNetworkConfig(_factory!, networkId);

        await using var alpha = new HydraTestClient(_factory!, new HydraConfig { Mode = Mode.Master, NetworkConfig = cfg, Name = "alpha" });
        await using var beta = new HydraTestClient(_factory!, new HydraConfig { Mode = Mode.Master, NetworkConfig = cfg, Name = "beta" });

        await alpha.StartAsync(CancellationToken.None);
        await beta.StartAsync(CancellationToken.None);
        await alpha.WaitForReady();
        await beta.WaitForReady();

        // alpha → beta
        await alpha.Send(["beta"], MessageSerializer.Encode(MessageKind.MouseMove, new MouseMoveMessage(1, 2)));
        var (fromAlpha, _, _) = await beta.WaitForMessage();
        Assert.That(fromAlpha, Is.EqualTo("alpha"));

        // beta → alpha
        await beta.Send(["alpha"], MessageSerializer.Encode(MessageKind.MouseMove, new MouseMoveMessage(3, 4)));
        var (fromBeta, _, _) = await alpha.WaitForMessage();
        Assert.That(fromBeta, Is.EqualTo("beta"));
    }

    [Test]
    public async Task NetworkIsolation_DifferentNetworks_CannotCommunicate()
    {
        var networkA = Guid.NewGuid();
        var networkB = Guid.NewGuid();
        var cfgA = await StyxTestServer.BuildNetworkConfig(_factory!, networkA);
        var cfgB = await StyxTestServer.BuildNetworkConfig(_factory!, networkB);

        await using var senderA = new HydraTestClient(_factory!, new HydraConfig { Mode = Mode.Master, NetworkConfig = cfgA, Name = "sender" });
        await using var receiverA = new HydraTestClient(_factory!, new HydraConfig { Mode = Mode.Master, NetworkConfig = cfgA, Name = "receiver" });
        await using var clientB = new HydraTestClient(_factory!, new HydraConfig { Mode = Mode.Master, NetworkConfig = cfgB, Name = "sender" });

        await senderA.StartAsync(CancellationToken.None);
        await receiverA.StartAsync(CancellationToken.None);
        await clientB.StartAsync(CancellationToken.None);
        await senderA.WaitForReady();
        await receiverA.WaitForReady();
        await clientB.WaitForReady();

        // clientB targets "receiver" — should not reach receiverA (different network)
        await clientB.Send(["receiver"], MessageSerializer.Encode(MessageKind.MouseMove, new MouseMoveMessage(7, 7)));

        Assert.ThrowsAsync<TimeoutException>(() => receiverA.WaitForMessage(800));
    }

    [Test]
    public async Task DuplicateHostname_OldConnectionKicked()
    {
        var networkId = Guid.NewGuid();
        var cfg = await StyxTestServer.BuildNetworkConfig(_factory!, networkId);

        await using var first = new HydraTestClient(_factory!, new HydraConfig { Mode = Mode.Master, NetworkConfig = cfg, Name = "duplicate" });
        await first.StartAsync(CancellationToken.None);
        await first.WaitForReady();

        await using var second = new HydraTestClient(_factory!, new HydraConfig { Mode = Mode.Master, NetworkConfig = cfg, Name = "duplicate" });
        await second.StartAsync(CancellationToken.None);
        await second.WaitForReady();

        var reason = await first.WaitForKick();
        Assert.That(reason, Is.EqualTo("duplicate hostname"));
    }

    [Test]
    public async Task PeersList_UpdatesOnConnectAndDisconnect()
    {
        var networkId = Guid.NewGuid();
        var cfg = await StyxTestServer.BuildNetworkConfig(_factory!, networkId);

        await using var clientA = new HydraTestClient(_factory!, new HydraConfig { Mode = Mode.Master, NetworkConfig = cfg, Name = "host-a" });
        await clientA.StartAsync(CancellationToken.None);
        await clientA.WaitForReady();

        var initialPeers = await clientA.WaitForPeers();
        Assert.That(initialPeers, Is.Empty);

        var clientB = new HydraTestClient(_factory!, new HydraConfig { Mode = Mode.Master, NetworkConfig = cfg, Name = "host-b" });
        await clientB.StartAsync(CancellationToken.None);
        await clientB.WaitForReady();

        var peersForA = await clientA.WaitForPeers();
        var peersForB = await clientB.WaitForPeers();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(peersForA, Contains.Item("host-b"));
            Assert.That(peersForB, Contains.Item("host-a"));
        }

        // disconnect B and verify A sees it leave
        await clientB.DisposeAsync();

        var finalPeers = await clientA.WaitForPeers();
        Assert.That(finalPeers, Is.Empty);
    }

    [Test]
    public async Task Encryption_RecoversAfterPeerReconnect()
    {
        var networkId = Guid.NewGuid();
        var key = StyxTestServer.GenerateEncryptionKey();
        var cfg = await StyxTestServer.BuildNetworkConfig(_factory!, networkId, key);

        await using var receiver = new HydraTestClient(_factory!, new HydraConfig { Mode = Mode.Master, NetworkConfig = cfg, Name = "receiver" });
        await receiver.StartAsync(CancellationToken.None);
        await receiver.WaitForReady();

        // sender1 connects and sends a message — receiver caches its RemoteKey
        var sender1 = new HydraTestClient(_factory!, new HydraConfig { Mode = Mode.Master, NetworkConfig = cfg, Name = "sender" });
        await sender1.StartAsync(CancellationToken.None);
        await sender1.WaitForReady();

        await sender1.Send(["receiver"], MessageSerializer.Encode(MessageKind.MouseMove, new MouseMoveMessage(1, 1)));
        var (_, kind1, _) = await receiver.WaitForMessage();
        Assert.That(kind1, Is.EqualTo(MessageKind.MouseMove));

        // sender disconnects; wait for receiver to observe the leave (ensures server processed disconnect)
        await sender1.DisposeAsync();
        await receiver.WaitForPeers(); // receiver sees sender leave

        // sender2 reconnects — new RelayEncryption means new salt, receiver's cached RemoteKey is stale
        var sender2 = new HydraTestClient(_factory!, new HydraConfig { Mode = Mode.Master, NetworkConfig = cfg, Name = "sender" });
        await sender2.StartAsync(CancellationToken.None);
        await sender2.WaitForReady();

        // receiver must re-derive remote key via ExtractKey — should succeed transparently
        await sender2.Send(["receiver"], MessageSerializer.Encode(MessageKind.MouseMove, new MouseMoveMessage(2, 2)));
        var (_, kind2, json2) = await receiver.WaitForMessage();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(kind2, Is.EqualTo(MessageKind.MouseMove));
            Assert.That(json2, Does.Contain("2"));
        }

        await sender2.DisposeAsync();
    }

    [Test]
    public async Task SendToUnknownHost_IsIgnored()
    {
        var networkId = Guid.NewGuid();
        var cfg = await StyxTestServer.BuildNetworkConfig(_factory!, networkId);

        await using var client = new HydraTestClient(_factory!, new HydraConfig { Mode = Mode.Master, NetworkConfig = cfg, Name = "solo" });
        await client.StartAsync(CancellationToken.None);
        await client.WaitForReady();

        // send to a host that doesn't exist — should silently no-op, not throw
        Assert.DoesNotThrowAsync(async () =>
            await client.Send(["nonexistent"], MessageSerializer.Encode(MessageKind.MouseMove, new MouseMoveMessage(0, 0))));
    }
}
