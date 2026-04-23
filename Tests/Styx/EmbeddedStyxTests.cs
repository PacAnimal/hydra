using Hydra.Config;
using Hydra.Relay;
using System.Net;
using System.Net.Sockets;
using Tests.Setup;

namespace Tests.Styx;

[TestFixture]
public class EmbeddedStyxTests
{
    private const string TestPassword = "embedded-test-password";

    private EmbeddedStyxServer? _server;
    private CancellationTokenSource? _cts;
    private int _port;
    private string _serverUrl = "";

    [SetUp]
    public async Task SetUp()
    {
        _port = FindFreePort();
        _serverUrl = $"http://localhost:{_port}";

        var config = new EmbeddedStyxServerConfig { Port = _port, Password = TestPassword };
        _cts = new CancellationTokenSource();
        _server = new EmbeddedStyxServer(config, TestLog.CreateLogger<EmbeddedStyxServer>());
        await _server.StartAsync(_cts.Token);
        await _server.WaitForReady();
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_server != null)
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await _server.StopAsync(stopCts.Token); }
            catch { /* ignore */ }
            _server.Dispose();
        }
        _cts?.Cancel();
        _cts?.Dispose();
    }

    private static EmbeddedHydraTestClient Client(string name, string? networkConfig = null)
        => new(TransitionTestHelper.Profile(name, new HydraConfig { Mode = Mode.Master, NetworkConfig = networkConfig }));

    private static async Task<EmbeddedHydraTestClient> ConnectedClient(string name, string networkConfig)
    {
        var client = Client(name, networkConfig);
        await client.StartAsync(CancellationToken.None);
        await client.WaitForReady();
        return client;
    }

    // computes a network config blob for the test server using the given password
    private ValueTask<string> Blob(string password = TestPassword) =>
        Common.NetworkConfig.ComputeEmbeddedBlob(_serverUrl, password);

    // ─── auth ────────────────────────────────────────────────────────────────

    [Test]
    public async Task Auth_CorrectPassword_Connects()
    {
        await using var client = await ConnectedClient("test-host", await Blob());
        Assert.That(client.IsConnected, Is.True);
    }

    [Test]
    public async Task Auth_WrongPassword_CannotConnect()
    {
        // wrong password means the authorization token decrypts to the wrong value on the server side
        await using var client = Client("test-host", await Blob("wrong-password"));
        await client.StartAsync(CancellationToken.None);

        Assert.ThrowsAsync<TimeoutException>(() => client.WaitForReady(1500));
    }

    // ─── messaging ───────────────────────────────────────────────────────────

    [Test]
    public async Task TwoClients_CanExchangeMessages()
    {
        var cfg = await Blob();

        await using var sender = await ConnectedClient("sender", cfg);
        await using var receiver = await ConnectedClient("receiver", cfg);

        var payload = MessageSerializer.Encode(MessageKind.MouseMove, new MouseMoveMessage("", 42, 99));
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
    public async Task TwoClients_BidirectionalExchange()
    {
        var cfg = await Blob();

        await using var alpha = await ConnectedClient("alpha", cfg);
        await using var beta = await ConnectedClient("beta", cfg);

        alpha.Send(["beta"], MessageSerializer.Encode(MessageKind.MouseMove, new MouseMoveMessage("", 1, 2)));
        var (fromAlpha, _, _) = await beta.WaitForMessage();
        Assert.That(fromAlpha, Is.EqualTo("alpha"));

        beta.Send(["alpha"], MessageSerializer.Encode(MessageKind.MouseMove, new MouseMoveMessage("", 3, 4)));
        var (fromBeta, _, _) = await alpha.WaitForMessage();
        Assert.That(fromBeta, Is.EqualTo("beta"));
    }

    // ─── peers ───────────────────────────────────────────────────────────────

    [Test]
    public async Task PeersList_UpdatesOnConnectAndDisconnect()
    {
        var cfg = await Blob();

        await using var clientA = await ConnectedClient("host-a", cfg);
        var initialPeers = await clientA.WaitForPeers();
        Assert.That(initialPeers, Is.Empty);

        var clientB = await ConnectedClient("host-b", cfg);
        var peersForA = await clientA.WaitForPeers();
        Assert.That(peersForA, Contains.Item("host-b"));

        await clientB.DisposeAsync();
        var finalPeers = await clientA.WaitForPeers();
        Assert.That(finalPeers, Is.Empty);
    }

    // ─── blob derivation ─────────────────────────────────────────────────────

    [Test]
    public async Task TwoBlobs_FromSamePassword_BothAuthenticate()
    {
        // two independently derived blobs from the same password must both authenticate
        // (each has a unique random nonce but both decrypt to the same network ID on the server)
        var cfg1 = await Blob();
        var cfg2 = await Blob();

        Assert.That(cfg1, Is.Not.EqualTo(cfg2), "blobs should differ due to random nonce");

        await using var client1 = await ConnectedClient("host-1", cfg1);
        await using var client2 = await ConnectedClient("host-2", cfg2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(client1.IsConnected, Is.True);
            Assert.That(client2.IsConnected, Is.True);
        }
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    private static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
