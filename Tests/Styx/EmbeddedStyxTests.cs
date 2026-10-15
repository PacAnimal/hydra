using Hydra.Config;
using Hydra.Relay;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Styx.Services;
using System.Net;
using System.Net.Sockets;
using Tests.Setup;
using StyxConstants = Styx.Constants;

namespace Tests.Styx;

[TestFixture]
public class EmbeddedStyxTests
{
    private const string TestPassword = "embedded-test-password";

    private EmbeddedStyxServer? _server;
    private CancellationTokenSource? _cts;
    private int _port;
    private string _serverUrl = "";
    private CapturingLog<EmbeddedStyxServer> _serverLog = new();

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
    public Task SetUp() => StartOnFreePort();

    private async Task StartOnFreePort(TimeProvider? throttleClock = null)
    {
        const int attempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await StartServer(FindFreePort(), throttleClock);
                return;
            }
            // ONLY a lost port race is retried. An undifferentiated catch would retry a bad password, a
            // malformed config — or a regression of the readiness HANG, which arrives here as the
            // TimeoutException from the bound above and would cost 5 x 30s per test before failing with a
            // message blaming the port.
            catch (Exception ex) when (attempt < attempts && IsPortUnavailable(ex))
            {
                TestContext.Out.WriteLine($"embedded styx lost the race for port {_port} (attempt {attempt}): {ex.Message}");
                await TearDown();
            }
        }
    }

    private async Task StartServer(int port, TimeProvider? throttleClock)
    {
        _port = port;
        _serverUrl = $"https://localhost:{_port}";
        _serverLog = new CapturingLog<EmbeddedStyxServer>();

        var config = new EmbeddedStyxServerConfig { Port = _port, Password = TestPassword };
        _cts = new CancellationTokenSource();
        _server = new EmbeddedStyxServer(config, _serverLog) { ThrottleClock = throttleClock ?? new InstantTimeProvider() };

        await _server.StartAsync(_cts.Token);
        await _server.WaitForReady().WaitAsync(TimeSpan.FromSeconds(60));
    }

    /// <summary>Whether a start failed because something else holds the port — the one thing worth retrying.</summary>
    private static bool IsPortUnavailable(Exception ex)
    {
        // kestrel nests it as IOException -> AddressInUseException -> SocketException
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
                return true;
        }
        return false;
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

        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        catch (ObjectDisposedException) { /* a retry already tore this one down */ }

        // NULLED, so a second TearDown — the retry calls this, and NUnit calls it again — cannot touch a
        // server it already stopped or a source it already disposed.
        _server = null;
        _cts = null;
    }

    private static EmbeddedHydraTestClient Client(string name, string? networkConfig = null, ILogger<RelayConnection>? log = null)
        => new(TransitionTestHelper.Profile(name, new HydraConfig { Mode = Mode.Master, NetworkConfig = networkConfig }), log);

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

    // the embedded host composes its own pipeline, so its auth filter is pinned separately from Styx's
    [Test]
    public async Task Send_WithoutAuth_FailsWithException()
    {
        await using var client = new TestStyxClient();
        await client.ConnectRaw(_serverUrl);

        var send = client.Server!.Send(["any-host"], [1, 2, 3]);

        Assert.That(async () => await send, Throws.Exception);
    }

    [Test]
    public async Task Auth_CorrectPassword_Connects()
    {
        await using var client = await ConnectedClient("test-host", await Blob());
        Assert.That(client.IsConnected, Is.True);
    }

    // the hub's WebSocket must ride a connector socket, or address preference and the per-address timeout skip it
    [Test]
    public async Task TheLiveConnectionRidesASocketFromTheRelayConnector()
    {
        await using var client = await ConnectedClient("test-host", await Blob());

        client.CloseConnectorSockets();

        await client.WaitForReady();
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

        sender.Send(["receiver"], TestMessages.Move(42, 99));

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

        alpha.Send(["beta"], TestMessages.Move(1, 2));
        var (fromAlpha, _, _) = await beta.WaitForMessage();
        Assert.That(fromAlpha, Is.EqualTo("alpha"));

        beta.Send(["alpha"], TestMessages.Move(3, 4));
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

    // ─── tls ─────────────────────────────────────────────────────────────────

    // an older Hydra reads why in its negotiate status, and this side says who it probably was, once
    [Test]
    public async Task PlainHttpIsAnsweredWithUpgradeRequiredAndOneWarning()
    {
        using var http = new HttpClient();
        using var first = await http.PostAsync($"http://localhost:{_port}/relay/negotiate", new ByteArrayContent([]));
        using var second = await http.PostAsync($"http://localhost:{_port}/relay/negotiate", new ByteArrayContent([]));

        var warnings = _serverLog.Entries.Where(e => e.Level == LogLevel.Warning && e.Message.Contains("older than 0.3")).ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.UpgradeRequired));
            Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.UpgradeRequired));
            Assert.That(warnings, Has.Length.EqualTo(1), "an old peer retrying every few seconds must not flood the log");
        }
    }

    [Test]
    public async Task TheBindingAnswersOnlyAProofForThePresentedCertificate()
    {
        var presented = await PresentedFingerprint();
        var proofs = EmbeddedRelayTls.ProofsFor(TestPassword, presented);

        using var valid = await PostBinding(proofs.Client);
        var answer = await valid.Content.ReadAsByteArrayAsync();
        using var wrongPassword = await PostBinding(EmbeddedRelayTls.ProofsFor("wrong-password", presented).Client);
        using var otherCertificate = await PostBinding(EmbeddedRelayTls.ProofsFor(TestPassword, new byte[32]).Client);
        using var oversized = await PostBinding([.. proofs.Client, 0]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(valid.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(answer, Is.EqualTo(proofs.Server));
            Assert.That(wrongPassword.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(otherCertificate.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized),
                "a proof made for an interceptor's cert must not open the relay");
            Assert.That(oversized.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        }
    }

    // nothing proxies the embedded relay, so a forwarded address is a claim anyone on a private network could make
    [Test]
    public async Task TheBindingCountsFailuresByTheConnectingAddressNotAForwardedOne()
    {
        var proofs = EmbeddedRelayTls.ProofsFor(TestPassword, await PresentedFingerprint());
        for (var i = 0; i < FailureLimiter.MaxFailures; i++)
            using (await PostBinding(new byte[EmbeddedRelayTls.ProofLength], forwardedFor: $"10.0.0.{i + 1}")) { }

        using var right = await PostBinding(proofs.Client);
        Assert.That(right.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), "each guess counted against an address it only claimed");
    }

    // a stranger's guess holds the gate for a throttle, but a peer that has proven itself is never queued behind it;
    // 127.0.0.1 and ::1 are two addresses to the relay, the peer and the stranger
    [Test]
    public async Task AKnownPeerIsAnsweredWhileAStrangersGuessHoldsTheGate()
    {
        var clock = new StartedTimers();
        await TearDown();
        await StartOnFreePort(clock);
        var proofs = EmbeddedRelayTls.ProofsFor(TestPassword, await PresentedFingerprint());
        var peer = $"https://127.0.0.1:{_port}";
        var stranger = $"https://[::1]:{_port}";
        using (var joined = await PostBinding(proofs.Client, server: peer))
            Assert.That(joined.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var guess = PostBinding(new byte[EmbeddedRelayTls.ProofLength], server: stranger);
        await clock.Started.Next(15000, "the guess's throttle");
        using var again = await PostBinding(proofs.Client, server: peer).WaitAsync(TimeSpan.FromSeconds(15));

        clock.Advance(TimeSpan.FromSeconds(StyxConstants.AuthThrottleSeconds));
        using var refused = await guess.WaitAsync(TimeSpan.FromSeconds(15));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(again.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(refused.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        }
    }

    // a cert ordinary validation trusts is a standalone Styx: the binding is dropped before it carries a proof
    [Test]
    public async Task ARelayWithATrustedCertificateIsSentNoProof()
    {
        await using var interceptor = new TlsInterceptor(_port);

        var proven = await EmbeddedRelayTls.ProveRelay(interceptor.Url, TestPassword, new SocketsHttpHandler(), TimeSpan.FromSeconds(10),
            CancellationToken.None, trusts: _ => true);

        Assert.That(proven, Is.Null);
    }

    // past the failure limit even a right proof is refused, so the binding cannot be used to guess online
    [Test]
    public async Task TheBindingStopsAnsweringAnAddressThatKeepsGuessing()
    {
        var proofs = EmbeddedRelayTls.ProofsFor(TestPassword, await PresentedFingerprint());
        for (var i = 0; i < FailureLimiter.MaxFailures; i++)
            using (await PostBinding(new byte[EmbeddedRelayTls.ProofLength])) { }

        using var right = await PostBinding(proofs.Client);
        Assert.That(right.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task ProvingTheRelayReturnsTheCertificateItPresented()
    {
        var presented = await PresentedFingerprint();

        var proven = await EmbeddedRelayTls.ProveRelay(_serverUrl, TestPassword, new SocketsHttpHandler(), TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.That(proven, Is.EqualTo(presented));
    }

    [Test]
    public void ProvingTheRelayWithTheWrongPasswordFails()
    {
        var refused = Assert.ThrowsAsync<HttpRequestException>(() =>
            EmbeddedRelayTls.ProveRelay(_serverUrl, "wrong-password", new SocketsHttpHandler(), TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.That(refused!.Message, Does.Contain("refused"));
    }

    // what a new Hydra says when the relay it reaches is an older one, speaking plain http
    [Test]
    public async Task ProvingAPlainHttpRelayNamesTheOlderHydra()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var answering = AnswerLikeAnOlderHydra(listener.AcceptSocketAsync());
        var url = $"https://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";

        var refused = Assert.ThrowsAsync<HttpRequestException>(() =>
            EmbeddedRelayTls.ProveRelay(url, TestPassword, new SocketsHttpHandler(), TimeSpan.FromSeconds(10), CancellationToken.None));
        await answering;

        Assert.That(refused!.Message, Does.Contain("older than 0.3"));
    }

    // what an older Hydra's plain-http relay says to a TLS client hello
    private static async Task AnswerLikeAnOlderHydra(Task<Socket> accepting)
    {
        using var socket = await accepting;
        await using var stream = new NetworkStream(socket);
        _ = await stream.ReadAtLeastAsync(new byte[1024], 1, throwOnEndOfStream: false);
        await stream.WriteAsync("HTTP/1.1 400 Bad Request\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"u8.ToArray());
    }

    // whoever answers with another cert gets one guessable proof and nothing more: never the hub, never the token
    [Test]
    public async Task AClientSendsAnInterceptorNothingButItsProof()
    {
        await using var interceptor = new TlsInterceptor(_port);
        await using var client = Client("test-host", await Common.NetworkConfig.ComputeEmbeddedBlob(interceptor.Url, TestPassword));
        await client.StartAsync(CancellationToken.None);

        var first = await interceptor.NextIntercepted();
        var second = await interceptor.NextIntercepted();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Does.StartWith($"POST {EmbeddedRelayTls.BindingPath} "));
            Assert.That(second, Does.StartWith($"POST {EmbeddedRelayTls.BindingPath} "), "the client went on past a binding that never answered");
            Assert.That(client.IsConnected, Is.False);
        }
    }

    // the cert proven by the binding is the only one the hub connection accepts
    [Test]
    public async Task AClientRefusesACertificateSwappedInAfterTheBinding()
    {
        await using var interceptor = new TlsInterceptor(_port, passThrough: 1);
        await using var client = Client("test-host", await Common.NetworkConfig.ComputeEmbeddedBlob(interceptor.Url, TestPassword));
        await client.StartAsync(CancellationToken.None);

        var afterBinding = await interceptor.NextIntercepted();
        var nextAttempt = await interceptor.NextIntercepted();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterBinding, Is.Null, "the hub connection accepted a cert the binding never proved");
            Assert.That(nextAttempt, Does.StartWith($"POST {EmbeddedRelayTls.BindingPath} "), "the next attempt must start over from the binding");
            Assert.That(await _server!.GetClients(), Is.Empty);
        }
    }

    // behind a forwarder that stays put, so the restarted relay can take a fresh port rather than race for the old one
    [Test]
    public async Task AClientFollowsARestartedRelayToItsNewCertificate()
    {
        await using var forwarder = new TlsInterceptor(_port, passThrough: int.MaxValue);
        await using var client = await ConnectedClient("test-host", await Common.NetworkConfig.ComputeEmbeddedBlob(forwarder.Url, TestPassword));
        var before = await PresentedFingerprint();

        await TearDown();
        await StartOnFreePort();
        forwarder.RetargetTo(_port);

        await client.WaitForReady();
        Assert.That(await PresentedFingerprint(), Is.Not.EqualTo(before), "each start makes its own cert");
    }

    // a relay that is not embedded keeps ordinary validation, which a self-signed cert fails
    [Test]
    public async Task ANonEmbeddedConfigRefusesASelfSignedCertificate()
    {
        var log = new CapturingLog<RelayConnection>();
        var external = (Common.NetworkConfig.Parse(await Blob()) with { Embedded = false }).ToBlob();
        await using var client = Client("test-host", external, log);
        await client.StartAsync(CancellationToken.None);

        await log.WaitFor(e => e.Level == LogLevel.Warning && e.Message.Contains("Relay connection failed"));

        Assert.That(await _server!.GetClients(), Is.Empty);
    }

    [Test]
    public async Task AnHttpAddressIsReachedOverTlsWithAWarning()
    {
        var log = new CapturingLog<RelayConnection>();
        await using var client = Client("test-host", await Common.NetworkConfig.ComputeEmbeddedBlob($"http://localhost:{_port}", TestPassword), log);
        await client.StartAsync(CancellationToken.None);

        await client.WaitForReady();

        Assert.That(log.Messages, Has.One.Contains("Change it to https://"));
    }

    [Test]
    public async Task AnHttpsAddressConnectsWithoutAWarning()
    {
        var log = new CapturingLog<RelayConnection>();
        await using var client = Client("test-host", await Blob(), log);
        await client.StartAsync(CancellationToken.None);

        await client.WaitForReady();

        Assert.That(log.Entries.Where(e => e.Level >= LogLevel.Warning), Is.Empty);
    }

    [TestCase("http://192.168.1.10:5000", "https://192.168.1.10:5000")]
    [TestCase("HTTP://relay", "https://relay")]
    [TestCase("https://relay:5000", "https://relay:5000")]
    [TestCase("192.168.1.10:5000", "https://192.168.1.10:5000")]
    [TestCase("styx.example.com:8080", "https://styx.example.com:8080")]
    public void WithHttpsUpgradesOnlyPlainHttp(string url, string expected) =>
        Assert.That(EmbeddedRelayTls.WithHttps(url), Is.EqualTo(expected));

    [Test]
    public void EachCertificateIsNew()
    {
        using var first = EmbeddedRelayTls.CreateCertificate();
        using var second = EmbeddedRelayTls.CreateCertificate();

        Assert.That(EmbeddedRelayTls.FingerprintOf(first), Is.Not.EqualTo(EmbeddedRelayTls.FingerprintOf(second)));
    }

    // the fingerprint of the cert the relay presents, read without trusting it
    private async Task<byte[]> PresentedFingerprint()
    {
        byte[]? presented = null;
        using var handler = new SocketsHttpHandler();
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
        {
            presented = EmbeddedRelayTls.FingerprintOf(certificate!);
            return true;
        };
        using var http = new HttpClient(handler);
        using var response = await http.GetAsync($"{_serverUrl}/");
        return presented!;
    }

    private async Task<HttpResponseMessage> PostBinding(byte[] proof, string? forwardedFor = null, string? server = null)
    {
        using var handler = new SocketsHttpHandler();
        handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        using var http = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{server ?? _serverUrl}{EmbeddedRelayTls.BindingPath}");
        request.Content = new ByteArrayContent(proof);
        if (forwardedFor != null) request.Headers.Add("X-Forwarded-For", forwardedFor);
        var response = await http.SendAsync(request);
        await response.Content.LoadIntoBufferAsync();
        return response;
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

        // TWO assertions, because one cannot tell the bug from the fix: `Throws.Exception` around a bounded
        // wait is satisfied by the TimeoutException the bound itself raises, so it passes whether readiness
        // faulted or hung.
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

    // the retry is only as good as this recognising a lost race in the shape kestrel actually throws it
    [Test]
    public void IsPortUnavailable_RecognisesKestrelsNestedAddressInUse()
    {
        var socket = new SocketException((int)SocketError.AddressAlreadyInUse);
        var kestrel = new IOException("Failed to bind to address", new AddressInUseException(socket.Message, socket));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(IsPortUnavailable(kestrel), Is.True);
            Assert.That(IsPortUnavailable(new IOException("disk on fire")), Is.False);
        }
    }

    private static int FindFreePort()

    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
    /// <summary>
    /// The embedded relay runs one invocation at a time per client on <c>/relay</c> and the default width on
    /// <c>/relay/parallel</c>, as <c>RelayLaneTests</c> pins on the standalone host. A master running an embedded relay
    /// is the deployment with no separate Styx at all, so a width that reaches one host and not the other reorders
    /// input for exactly those users. Built rather than started: <c>Build()</c> composes the options and binds no port.
    /// </summary>
    [Test]
    public void TheEmbeddedRelayRunsOneInvocationAtATimeOnItsOrderedPath()
    {
        var config = new EmbeddedStyxServerConfig { Port = 0, Password = TestPassword };
        using var server = new EmbeddedStyxServer(config, NullLogger<EmbeddedStyxServer>.Instance);
        using var app = server.BuildApp();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(RelayWidths.OrderedPath(app.Services), Is.All.EqualTo(1),
                "the input lane does not wait for each Send, so only one invocation at a time keeps its frames in order");
            Assert.That(RelayWidths.ParallelPath(app.Services), Is.EqualTo(global::Styx.Constants.DefaultParallelInvocations));
        }
    }
}
