using Cathedral.Styx;
using Common.DTO;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Styx.Services;
using Tests.Setup;

namespace Tests.Styx;

/// <summary>A token's <c>exp</c> evicts the registration it admitted, and only that one, on a clock the test steps.</summary>
[TestFixture]
public class StyxExpiryTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private WebApplicationFactory<global::Styx.Program>? _factory;

    private WebApplicationFactory<global::Styx.Program> Factory => _factory!;

    [OneTimeSetUp]
    public void Start()
    {
        _factory = StyxTestServer.Create(configure: services => services.AddSingleton<TimeProvider>(_clock));
        _ = _factory.Server;
    }

    [OneTimeTearDown]
    public async Task Stop()
    {
        if (_factory != null) await _factory.DisposeAsync();
    }

    [Test]
    public async Task AtExpiry_ThePeerIsKicked_AndItsPeersSeeItLeave()
    {
        var network = StyxToken.NewNetworkId();
        await using var watcher = new TestStyxClient();
        await using var expiring = new TestStyxClient();
        await watcher.Connect(Factory, StyxTestServer.MintToken(network).ToString(), "watcher");
        await expiring.Connect(Factory, Expiring(network, TimeSpan.FromHours(1)), "expiring");
        await WaitForPeers(watcher, ["expiring"]);

        _clock.Advance(TimeSpan.FromHours(1));
        Assert.That(await expiring.WaitForKick(), Is.EqualTo(ExpiryScheduler.ExpiredReason));
        await WaitForPeers(watcher, []);
    }

    // a peer on the parallel path is evicted as one on /relay is: through the relay's one set of connections
    [Test]
    public async Task AtExpiry_APeerOnTheParallelPathIsKicked()
    {
        await using var expiring = new TestStyxClient();
        await expiring.Connect(Factory, Expiring(StyxToken.NewNetworkId(), TimeSpan.FromHours(1)), "expiring", global::Styx.Constants.ParallelRelayPath);

        _clock.Advance(TimeSpan.FromHours(1));
        Assert.That(await expiring.WaitForKick(), Is.EqualTo(ExpiryScheduler.ExpiredReason));
    }

    [Test]
    public async Task BeforeExpiry_NothingHappens()
    {
        await using var client = new TestStyxClient();
        await client.Connect(Factory, Expiring(StyxToken.NewNetworkId(), TimeSpan.FromHours(1)), "early");
        _clock.Advance(TimeSpan.FromMinutes(59));
        Assert.That(await Kicked(client), Is.False);
    }

    [Test]
    public async Task ReAuthenticatingToALaterExpiry_IsNotEvictedByTheEarlierTimer()
    {
        var network = StyxToken.NewNetworkId();
        await using var client = new TestStyxClient();
        await client.Connect(Factory, Expiring(network, TimeSpan.FromHours(1)), "renewing");
        await Login(client, Expiring(network, TimeSpan.FromHours(10)), "renewing");

        _clock.Advance(TimeSpan.FromHours(2));
        Assert.That(await Kicked(client), Is.False);
        _clock.Advance(TimeSpan.FromHours(8));
        Assert.That(await client.WaitForKick(), Is.EqualTo(ExpiryScheduler.ExpiredReason));
    }

    [Test]
    public async Task ReAuthenticatingToAShorterExpiry_IsEvictedAtTheShorterOne()
    {
        var network = StyxToken.NewNetworkId();
        await using var client = new TestStyxClient();
        await client.Connect(Factory, Expiring(network, TimeSpan.FromHours(10)), "shortening");
        await Login(client, Expiring(network, TimeSpan.FromHours(1)), "shortening");

        _clock.Advance(TimeSpan.FromHours(1));
        Assert.That(await client.WaitForKick(), Is.EqualTo(ExpiryScheduler.ExpiredReason));
    }

    [Test]
    public async Task ReAuthenticatingWithoutExpiry_CancelsTheEviction()
    {
        var network = StyxToken.NewNetworkId();
        await using var client = new TestStyxClient();
        await client.Connect(Factory, Expiring(network, TimeSpan.FromHours(1)), "forever");
        await Login(client, StyxTestServer.MintToken(network).ToString(), "forever");

        _clock.Advance(TimeSpan.FromHours(2));
        Assert.That(await Kicked(client), Is.False);
    }

    [Test]
    public async Task AFailedReAuthentication_DoesNotCancelTheEviction()
    {
        var network = StyxToken.NewNetworkId();
        await using var client = new TestStyxClient();
        await client.Connect(Factory, Expiring(network, TimeSpan.FromHours(1)), "failing");
        var refused = await Login(client, "styx2.not-a-token", "failing");
        Assume.That(refused.Authenticated, Is.False);

        _clock.Advance(TimeSpan.FromHours(1));
        Assert.That(await client.WaitForKick(), Is.EqualTo(ExpiryScheduler.ExpiredReason));
    }

    [Test]
    public async Task ADisplacedPeer_IsNotKickedAgainAtItsExpiry_AndItsReplacementStays()
    {
        var network = StyxToken.NewNetworkId();
        await using var first = new TestStyxClient();
        await using var second = new TestStyxClient();
        await first.Connect(Factory, Expiring(network, TimeSpan.FromHours(1)), "seat");
        await second.Connect(Factory, StyxTestServer.MintToken(network).ToString(), "seat");
        Assert.That(await first.WaitForKick(), Is.EqualTo("duplicate hostname"));

        _clock.Advance(TimeSpan.FromHours(1));
        await first.Server!.Ping();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.KickReason, Is.EqualTo("duplicate hostname"), "kicked once, not again at expiry");
            Assert.That(await Kicked(second), Is.False);
        }
    }

    [Test]
    public async Task AnExpiryMoreThanFiftyDaysOut_StillFires()
    {
        await using var client = new TestStyxClient();
        await client.Connect(Factory, Expiring(StyxToken.NewNetworkId(), TimeSpan.FromDays(60)), "patient");
        _clock.Advance(TimeSpan.FromDays(59));
        Assert.That(await Kicked(client), Is.False);
        _clock.Advance(TimeSpan.FromDays(1));
        Assert.That(await client.WaitForKick(), Is.EqualTo(ExpiryScheduler.ExpiredReason));
    }

    [Test]
    public async Task AKickedPeer_CannotSend()
    {
        var network = StyxToken.NewNetworkId();
        await using var listener = new TestStyxClient();
        await using var expiring = new TestStyxClient();
        await listener.Connect(Factory, StyxTestServer.MintToken(network).ToString(), "listener");
        await expiring.Connect(Factory, Expiring(network, TimeSpan.FromHours(1)), "expiring");
        _clock.Advance(TimeSpan.FromHours(1));
        await expiring.WaitForKick();

        var server = expiring.Server!;
        Assert.That(async () => await server.Send(["listener"], [1]), Throws.Exception);
        Assert.That(listener.LastReceived, Is.Null);
    }

    private string Expiring(Guid network, TimeSpan after) =>
        StyxTestServer.MintToken(network, StyxCaveat.Expires(_clock.GetUtcNow() + after)).ToString();

    private static Task<RelayLoginResponse> Login(TestStyxClient client, string authorization, string hostName) =>
        client.Server!.Authenticate(new RelayLogin { Authorization = authorization, HostName = hostName });

    // A kick is sent before the clock call returns its timers' work, but delivered asynchronously. A round trip
    // through the hub on the same connection comes back only after anything the relay sent before it.
    private static async Task<bool> Kicked(TestStyxClient client)
    {
        await client.Server!.Ping();
        return client.KickReason is not null;
    }

    private static async Task WaitForPeers(TestStyxClient client, string[] expected)
    {
        while (!(await client.WaitForPeers()).SequenceEqual(expected)) { }
    }
}
