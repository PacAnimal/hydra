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

    /// <summary>
    /// Starts the relay on a free port, RETRYING on a port that stopped being free.
    ///
    /// <para><c>FindFreePort</c> asks the OS for one and closes the listener, so between that and Kestrel
    /// binding it, anything else on a busy machine may take it — a real race, not a theoretical one, and the
    /// busier the machine the likelier. Each attempt draws a FRESH port, because retrying the same one is
    /// retrying the thing that just failed.</para>
    ///
    /// <para>This only became visible once <see cref="EmbeddedStyxServer.WaitForReady"/> started faulting on
    /// a failed start. Before that, losing the race hung the run instead of failing it — thirty four minutes
    /// of a lane with nothing to show for it.</para>
    /// </summary>
    [SetUp]
    public async Task SetUp()
    {
        const int attempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            _port = FindFreePort();
            _serverUrl = $"http://localhost:{_port}";

            var config = new EmbeddedStyxServerConfig { Port = _port, Password = TestPassword };
            _cts = new CancellationTokenSource();
            _server = new EmbeddedStyxServer(config, TestLog.CreateLogger<EmbeddedStyxServer>());

            try
            {
                await _server.StartAsync(_cts.Token);
                await _server.WaitForReady().WaitAsync(TimeSpan.FromSeconds(30));
                return;
            }
            catch (Exception ex) when (attempt < attempts)
            {
                TestContext.Out.WriteLine($"embedded styx would not start on port {_port} (attempt {attempt}): {ex.Message}");
                await TearDown();
            }
        }
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

        Exception? ex = null;
        try { await client.WaitForReady(1500); } catch (TimeoutException e) { ex = e; }
        Assert.That(ex, Is.InstanceOf<TimeoutException>());
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

    /// <summary>
    /// A relay that cannot start FAILS its readiness rather than leaving the caller waiting.
    ///
    /// <para><b>A hang is the worst shape this failure can take, and it was the shape it had.</b>
    /// <c>WaitForReady</c> completed only on success, so anything that threw on the way up — a port taken
    /// between being probed and being bound, or a listener that could not even be described — left every
    /// awaiter on a task nothing would ever finish. The hosted service
    /// logged the failure and the test sat there; a whole CI lane was killed after thirty four minutes with
    /// no idea which test it was in.</para>
    ///
    /// <para>The failure here is certain rather than raced, and certain on every platform — see the port.</para>
    /// </summary>
    [Test]
    public async Task ARelayThatCannotStartFailsItsReadinessInsteadOfHanging()
    {
        // A port number that cannot exist, rather than one held by a squatter. Holding a port is NOT a
        // portable way to make a bind fail: on Windows the relay binds it anyway and starts happily, so
        // that version of this test passed there for the wrong reason while failing to prove anything. An
        // out-of-range port fails while the listener is merely being DESCRIBED, identically everywhere.
        var config = new EmbeddedStyxServerConfig { Port = 70000, Password = TestPassword };
        using var cts = new CancellationTokenSource();
        using var blocked = new EmbeddedStyxServer(config, TestLog.CreateLogger<EmbeddedStyxServer>());

        try { await blocked.StartAsync(cts.Token); } catch { /* the hosted service may surface it here too */ }

        // TWO assertions, because one cannot tell the bug from the fix. `Throws.Exception` around a
        // `WaitAsync(20s)` is satisfied by the TimeoutException that the bound ITSELF raises — so the test
        // passes whether readiness faulted or hung, which is exactly the defect it exists to catch. Verified:
        // with the fault removed, that version still passed.
        var ready = blocked.WaitForReady();
        var settled = await Task.WhenAny(ready, Task.Delay(TimeSpan.FromSeconds(20)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(settled, Is.SameAs(ready),
                "a relay that could not start left its readiness waiting for ever — the hang this test exists for");
            Assert.That(ready.IsFaulted, Is.True,
                "readiness must report the failure; completing it successfully would be worse than the hang");
        }

        await cts.CancelAsync();
        try { await blocked.StopAsync(CancellationToken.None); } catch { /* best effort */ }
    }

    private static int FindFreePort()

    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
