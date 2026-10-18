using Cathedral.Extensions;
using Common;
using Hydra.Relay;
using Styx;
using Tests.Setup;

namespace Tests.Styx;

[TestFixture]
public class StyxIntegrationTests : StyxFixtureBase
{
    // ─── auth tests (TestStyxClient — controls the auth step directly) ───────

    [Test]
    public async Task Auth_WrongPassword_ReturnsUnauthenticated()
    {
        await using var client = new TestStyxClient();
        var auth = await StyxTestServer.GenerateAuthorization(Guid.NewGuid(), password: "wrong-password");
        var response = await client.Connect(Factory, auth, "test-host");
        Assert.That(response.Authenticated, Is.False);
    }

    [Test]
    public async Task Auth_CorrectPassword_Authenticates()
    {
        await using var client = new TestStyxClient();
        var auth = await StyxTestServer.GenerateAuthorization(Guid.NewGuid());
        var response = await client.Connect(Factory, auth, "test-host");
        Assert.That(response.Authenticated, Is.True);
    }

    [TestCase(global::Styx.Constants.RelayPath)]
    [TestCase(global::Styx.Constants.ParallelRelayPath)]
    public async Task Ping_WithoutAuth_ReturnsPong(string path)
    {
        await using var client = new TestStyxClient();
        await client.ConnectRaw(Factory, path);
        var result = await client.Server!.Ping();
        Assert.That(result, Is.True);
    }

    [TestCase(global::Styx.Constants.RelayPath)]
    [TestCase(global::Styx.Constants.ParallelRelayPath)]
    public async Task Send_WithoutAuth_FailsWithException(string path)
    {
        // hub filter calls Context.Abort() on unauthenticated calls — client sees a cancellation or error
        await using var client = new TestStyxClient();
        await client.ConnectRaw(Factory, path);
        Exception? ex = null;
        try { await client.Server!.Send(["any-host"], [1, 2, 3]); } catch (Exception e) { ex = e; }
        Assert.That(ex, Is.Not.Null);
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

        var respA = await clientA.Connect(Factory, authA, "host");
        var respB = await clientB.Connect(Factory, authB, "host"); // same hostname, different network

        using (Assert.EnterMultipleScope())
        {
            Assert.That(respA.Authenticated, Is.True);
            Assert.That(respB.Authenticated, Is.True);
        }
    }

    [Test]
    public async Task Send_MatchesTheTargetHostCaseInsensitively()
    {
        var auth = await StyxTestServer.GenerateAuthorization(Guid.NewGuid());
        await using var sender = new TestStyxClient();
        await using var receiver = new TestStyxClient();
        using (Assert.EnterMultipleScope())
        {
            Assert.That((await receiver.Connect(Factory, auth, "Receiver-Host")).Authenticated, Is.True);
            Assert.That((await sender.Connect(Factory, auth, "sender-host")).Authenticated, Is.True);
        }

        await sender.Server!.Send(["RECEIVER-HOST"], [1, 2, 3]);

        var (source, _, payload) = await receiver.WaitForReceive();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source, Is.EqualTo("sender-host"));
            Assert.That(payload, Is.EqualTo(new byte[] { 1, 2, 3 }));
        }
    }

    // ─── relay tests (HydraTestClient — real Hydra relay code + RelayEncryption) ─

    [Test]
    public async Task TwoHydraClients_MessageIntegrity()
    {
        await using var pair = await ConnectedPair();
        var (sender, receiver) = pair;

        var payload = TestMessages.Move(42, 99);
        sender.Send(["receiver"], payload);

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
    public async Task TwoHydraClients_ReliableSendCompletesAndDelivers()
    {
        await using var pair = await ConnectedPair();
        var (sender, receiver) = pair;

        var payload = MessageSerializer.Encode(MessageKind.FileTransferChunk, new FileTransferChunkMessage(0, [1, 2, 3]));
        await sender.SendReliableAsync(["receiver"], payload).AsTask().WaitAsync(Bound);

        var (source, kind, _) = await receiver.WaitForMessage();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source, Is.EqualTo("sender"));
            Assert.That(kind, Is.EqualTo(MessageKind.FileTransferChunk));
        }
    }

    [Test]
    public async Task HydraClient_SuspendClosesConnectionUntilResume()
    {
        var networkId = Guid.NewGuid();
        var cfg = await StyxTestServer.BuildNetworkConfig(Factory, networkId);

        await using var client = await HydraTestClient.Started(HydraTestClient.Master(Factory, "sleeping-host", cfg));

        using (var timeout = new CancellationTokenSource(Bound))
            await client.SuspendConnection(timeout.Token);

        Assert.That(client.IsConnected, Is.False);
        await client.WaitUntilParked();
        Assert.That(client.IsConnected, Is.False, "suspended relay should not reconnect in the background");

        client.ResumeConnection();
        await client.WaitForReady();
        Assert.That(client.IsConnected, Is.True);
    }

    [Test]
    public async Task TwoHydraClients_BidirectionalExchange()
    {
        await using var pair = await ConnectedPair("alpha", "beta");
        var (alpha, beta) = pair;

        // alpha → beta
        alpha.Send(["beta"], TestMessages.Move(1, 2));
        var (fromAlpha, _, _) = await beta.WaitForMessage();
        Assert.That(fromAlpha, Is.EqualTo("alpha"));

        // beta → alpha
        beta.Send(["alpha"], TestMessages.Move(3, 4));
        var (fromBeta, _, _) = await alpha.WaitForMessage();
        Assert.That(fromBeta, Is.EqualTo("beta"));
    }

    [Test]
    public async Task TwoHydraClients_SendMouseDelta_DeliversAsMouseMoveDelta()
    {
        await using var pair = await ConnectedPair();
        var (sender, receiver) = pair;

        // exercises IRelaySender.SendMouseDelta end to end through a real RelayConnection: the caller
        // passes ints, not a pre-encoded payload, so this proves the encode-on-send path is wired up.
        sender.SendMouseDelta(["receiver"], 17, -9);

        var (source, kind, json) = await receiver.WaitForMessage();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(source, Is.EqualTo("sender"));
            Assert.That(kind, Is.EqualTo(MessageKind.MouseMoveDelta));
            var decoded = json.FromSaneJson<MouseMoveDeltaMessage>();
            Assert.That(decoded, Is.Not.Null);
            Assert.That(decoded!.Dx, Is.EqualTo(17));
            Assert.That(decoded.Dy, Is.EqualTo(-9));
        }
    }

    [Test]
    public async Task TwoHydraClients_RapidMouseDeltas_SumSurvivesRegardlessOfHowMuchCoalesces()
    {
        await using var pair = await ConnectedPair();
        var (sender, receiver) = pair;

        // How many of these collapse into one wire message depends on real thread scheduling against the
        // live drain loop — that's inherent to best-effort coalescing, not something a test can pin down.
        // What must always hold regardless: every dx/dy sent is accounted for exactly once on arrival,
        // and coalescing can only ever reduce the message count, never inflate it.
        const int sendCount = 200;
        var expectedDx = 0;
        var expectedDy = 0;
        for (var i = 1; i <= sendCount; i++)
        {
            sender.SendMouseDelta(["receiver"], i, -i);
            expectedDx += i;
            expectedDy -= i;
        }
        sender.Send(["receiver"], TestMessages.Sentinel());

        var receivedDx = 0;
        var receivedDy = 0;
        var messageCount = 0;
        foreach (var (_, kind, json) in await receiver.ReadUntilSentinel())
        {
            Assert.That(kind, Is.EqualTo(MessageKind.MouseMoveDelta));
            var decoded = json.FromSaneJson<MouseMoveDeltaMessage>();
            Assert.That(decoded, Is.Not.Null);
            receivedDx += decoded!.Dx;
            receivedDy += decoded.Dy;
            messageCount++;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(messageCount, Is.GreaterThan(0), "expected at least one delivered message");
            Assert.That(messageCount, Is.LessThanOrEqualTo(sendCount),
                "coalescing must never produce more messages than were sent");
            Assert.That(receivedDx, Is.EqualTo(expectedDx),
                "no dx may be lost or duplicated across however many messages the batching produced");
            Assert.That(receivedDy, Is.EqualTo(expectedDy),
                "no dy may be lost or duplicated across however many messages the batching produced");
        }
    }

    [Test]
    public async Task NetworkIsolation_DifferentNetworks_CannotCommunicate()
    {
        var networkA = Guid.NewGuid();
        var networkB = Guid.NewGuid();
        // one key for both, so a frame leaked across networks would decrypt and arrive rather than be discarded
        var key = NetworkConfig.NewEncryptionKey();
        var cfgA = await StyxTestServer.BuildNetworkConfig(Factory, networkA, key);
        var cfgB = await StyxTestServer.BuildNetworkConfig(Factory, networkB, key);

        await using var pairA = await ClientPair.Start(HydraTestClient.Master(Factory, "sender", cfgA), HydraTestClient.Master(Factory, "receiver", cfgA));
        await using var clientB = await HydraTestClient.Started(HydraTestClient.Master(Factory, "sender", cfgB));

        // clientB targets "receiver" — should not reach pairA's receiver (different network)
        clientB.Send(["receiver"], TestMessages.Move(7));

        // clientB's own echo means the relay has handled the stray send; a sentinel queued after it then arrives first
        clientB.Send(["sender"], TestMessages.Move(8));
        await clientB.WaitForMessage();
        pairA.Sender.Send(["receiver"], TestMessages.Move(9));

        var (source, _, json) = await pairA.Receiver.WaitForMessage();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source, Is.EqualTo("sender"));
            Assert.That(json.FromSaneJson<MouseMoveMessage>()!.X, Is.EqualTo(9));
        }
    }

    [Test]
    public async Task DuplicateHostname_OldConnectionKicked()
    {
        var networkId = Guid.NewGuid();
        var cfg = await StyxTestServer.BuildNetworkConfig(Factory, networkId);

        await using var first = await HydraTestClient.Started(HydraTestClient.Master(Factory, "duplicate", cfg));

        await using var second = await HydraTestClient.Started(HydraTestClient.Master(Factory, "duplicate", cfg));

        var reason = await first.WaitForKick();
        Assert.That(reason, Is.EqualTo(RelayReasons.KickedDuplicateHostname));
    }

    [Test]
    public async Task DuplicateHostname_ObserverSeesHostLeaveThenReturn()
    {
        var networkId = Guid.NewGuid();
        var auth = await StyxTestServer.GenerateAuthorization(networkId);

        await using var observer = new TestStyxClient();
        using (Assert.EnterMultipleScope())
        {
            Assert.That((await observer.Connect(Factory, auth, "observer")).Authenticated, Is.True);
            Assert.That(await observer.WaitForPeers(), Is.Empty);
        }

        await using var original = new TestStyxClient();
        using (Assert.EnterMultipleScope())
        {
            Assert.That((await original.Connect(Factory, auth, "roamer")).Authenticated, Is.True);
            Assert.That(await observer.WaitForPeers(), Is.EqualTo(["roamer"]));
        }

        // the same host authenticates on a fresh connection, as it does when a half-open link is replaced
        // before the server has noticed the old one died
        await using var reconnected = new TestStyxClient();
        using (Assert.EnterMultipleScope())
        {
            Assert.That((await reconnected.Connect(Factory, auth, "roamer")).Authenticated, Is.True);
            Assert.That(await original.WaitForKick(), Is.EqualTo(RelayReasons.KickedDuplicateHostname));

            // both halves, in order — an observer given one identical list has no way to tell this happened
            Assert.That(await observer.WaitForPeers(), Is.Empty, "displaced host should be broadcast as gone first");
        }
        Assert.That(await observer.WaitForPeers(), Is.EqualTo(["roamer"]), "and then as present again");
    }

    [Test]
    public async Task PeersList_UpdatesOnConnectAndDisconnect()
    {
        var networkId = Guid.NewGuid();
        var cfg = await StyxTestServer.BuildNetworkConfig(Factory, networkId);

        await using var clientA = await HydraTestClient.Started(HydraTestClient.Master(Factory, "host-a", cfg));

        var initialPeers = await clientA.WaitForPeers();
        Assert.That(initialPeers, Is.Empty);

        await using (var clientB = await HydraTestClient.Started(HydraTestClient.Master(Factory, "host-b", cfg)))
        {
            var peersForA = await clientA.WaitForPeers();
            var peersForB = await clientB.WaitForPeers();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(peersForA, Contains.Item("host-b"));
                Assert.That(peersForB, Contains.Item("host-a"));
            }
        }

        // B is disconnected; verify A sees it leave
        var finalPeers = await clientA.WaitForPeers();
        Assert.That(finalPeers, Is.Empty);
    }

    [Test]
    public async Task Encryption_RecoversAfterPeerReconnect()
    {
        var networkId = Guid.NewGuid();
        var key = NetworkConfig.NewEncryptionKey();
        var cfg = await StyxTestServer.BuildNetworkConfig(Factory, networkId, key);

        await using var receiver = await HydraTestClient.Started(HydraTestClient.Master(Factory, "receiver", cfg));

        // sender1 connects and sends a message — receiver caches its RemoteKey
        await using (var sender1 = await HydraTestClient.Started(HydraTestClient.Master(Factory, "sender", cfg)))
        {
            sender1.Send(["receiver"], TestMessages.Move(1));
            var (_, kind1, _) = await receiver.WaitForMessage();
            Assert.That(kind1, Is.EqualTo(MessageKind.MouseMove));
        }

        // sender disconnected; wait for receiver to observe the leave (ensures server processed disconnect)
        await receiver.WaitForPeers(); // receiver sees sender leave

        // sender2 reconnects — new RelayEncryption means new salt, receiver's cached RemoteKey is stale
        await using var sender2 = await HydraTestClient.Started(HydraTestClient.Master(Factory, "sender", cfg));

        // receiver must re-derive remote key via ExtractKey — should succeed transparently
        sender2.Send(["receiver"], TestMessages.Move(2));
        var (_, kind2, json2) = await receiver.WaitForMessage();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(kind2, Is.EqualTo(MessageKind.MouseMove));
            Assert.That(json2, Does.Contain("2"));
        }
    }

    [Test]
    public async Task SendToUnknownHost_IsIgnored()
    {
        var networkId = Guid.NewGuid();
        var cfg = await StyxTestServer.BuildNetworkConfig(Factory, networkId);

        await using var client = await HydraTestClient.Started(HydraTestClient.Master(Factory, "solo", cfg));

        // send to a host that doesn't exist — should silently no-op, not throw
        client.Send(["nonexistent"], TestMessages.Move(0));
    }
}
