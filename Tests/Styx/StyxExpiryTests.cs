using Cathedral.Styx;
using Common.DTO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Styx;
using Tests.Setup;

namespace Tests.Styx;

/// <summary>A token's <c>exp</c> evicts the registration it admitted, and only that one, on a clock the test steps.</summary>
[TestFixture]
public class StyxExpiryTests : StyxFixtureBase
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));

    protected override void ConfigureRelay(IServiceCollection services) => services.AddSingleton<TimeProvider>(_clock);

    [Test]
    public async Task AtExpiry_ThePeerIsKicked_AndItsPeersSeeItLeave()
    {
        var network = StyxToken.NewNetworkId();
        await using var watcher = new TestStyxClient();
        await using var expiring = new TestStyxClient();
        await watcher.Connect(Factory, StyxTestServer.MintToken(network).ToString(), "watcher");
        await expiring.Connect(Factory, Expiring(network, TimeSpan.FromHours(1)), "expiring");
        await watcher.WaitForPeers(["expiring"]);

        _clock.Advance(TimeSpan.FromHours(1));
        Assert.That(await expiring.WaitForKick(), Is.EqualTo(RelayReasons.KickedExpired));
        await watcher.WaitForPeers([]);
    }

    // a peer on the parallel path is evicted as one on /relay is: through the relay's one set of connections
    [Test]
    public async Task AtExpiry_APeerOnTheParallelPathIsKicked()
    {
        await using var expiring = new TestStyxClient();
        await expiring.Connect(Factory, Expiring(StyxToken.NewNetworkId(), TimeSpan.FromHours(1)), "expiring", Constants.ParallelRelayPath);

        _clock.Advance(TimeSpan.FromHours(1));
        Assert.That(await expiring.WaitForKick(), Is.EqualTo(RelayReasons.KickedExpired));
    }

    [Test]
    public async Task BeforeExpiry_NothingHappens()
    {
        var network = StyxToken.NewNetworkId();
        await using var client = new TestStyxClient();
        await client.Connect(Factory, Expiring(network, TimeSpan.FromHours(1)), "early");
        _clock.Advance(TimeSpan.FromMinutes(59));
        await AssertNotKicked(client, network, "early");
    }

    [Test]
    public async Task ReAuthenticatingToALaterExpiry_IsNotEvictedByTheEarlierTimer()
    {
        var network = StyxToken.NewNetworkId();
        await using var client = new TestStyxClient();
        await client.Connect(Factory, Expiring(network, TimeSpan.FromHours(1)), "renewing");
        await Login(client, Expiring(network, TimeSpan.FromHours(10)), "renewing");

        _clock.Advance(TimeSpan.FromHours(2));
        await AssertNotKicked(client, network, "renewing");
        _clock.Advance(TimeSpan.FromHours(8));
        Assert.That(await client.WaitForKick(), Is.EqualTo(RelayReasons.KickedExpired));
    }

    [Test]
    public async Task ReAuthenticatingToAShorterExpiry_IsEvictedAtTheShorterOne()
    {
        var network = StyxToken.NewNetworkId();
        await using var client = new TestStyxClient();
        await client.Connect(Factory, Expiring(network, TimeSpan.FromHours(10)), "shortening");
        await Login(client, Expiring(network, TimeSpan.FromHours(1)), "shortening");

        _clock.Advance(TimeSpan.FromHours(1));
        Assert.That(await client.WaitForKick(), Is.EqualTo(RelayReasons.KickedExpired));
    }

    [Test]
    public async Task ReAuthenticatingWithoutExpiry_CancelsTheEviction()
    {
        var network = StyxToken.NewNetworkId();
        await using var client = new TestStyxClient();
        await client.Connect(Factory, Expiring(network, TimeSpan.FromHours(1)), "forever");
        await Login(client, StyxTestServer.MintToken(network).ToString(), "forever");

        _clock.Advance(TimeSpan.FromHours(2));
        await AssertNotKicked(client, network, "forever");
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
        Assert.That(await client.WaitForKick(), Is.EqualTo(RelayReasons.KickedExpired));
    }

    [Test]
    public async Task ADisplacedPeer_IsNotKickedAgainAtItsExpiry_AndItsReplacementStays()
    {
        var network = StyxToken.NewNetworkId();
        await using var first = new TestStyxClient();
        await using var second = new TestStyxClient();
        await first.Connect(Factory, Expiring(network, TimeSpan.FromHours(1)), "seat");
        await second.Connect(Factory, StyxTestServer.MintToken(network).ToString(), "seat");
        Assert.That(await first.WaitForKick(), Is.EqualTo(RelayReasons.KickedDuplicateHostname));

        _clock.Advance(TimeSpan.FromHours(1));
        await first.Server!.Ping();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.KickReason, Is.EqualTo(RelayReasons.KickedDuplicateHostname), "kicked once, not again at expiry");
            await AssertNotKicked(second, network, "seat");
        }
    }

    [Test]
    public async Task AnExpiryMoreThanFiftyDaysOut_StillFires()
    {
        var network = StyxToken.NewNetworkId();
        await using var client = new TestStyxClient();
        await client.Connect(Factory, Expiring(network, TimeSpan.FromDays(60)), "patient");
        _clock.Advance(TimeSpan.FromDays(59));
        await AssertNotKicked(client, network, "patient");
        _clock.Advance(TimeSpan.FromDays(1));
        Assert.That(await client.WaitForKick(), Is.EqualTo(RelayReasons.KickedExpired));
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
        await using var witness = await Witness(network);
        await witness.Server!.Send(["listener"], [0]);
        Assert.That((await listener.WaitForReceive()).Source, Is.EqualTo(WitnessName), "the kicked peer's send was delivered");
    }

    private string Expiring(Guid network, TimeSpan after) =>
        StyxTestServer.MintToken(network, StyxCaveat.Expires(_clock.GetUtcNow() + after)).ToString();

    private static Task<RelayLoginResponse> Login(TestStyxClient client, string authorization, string hostName) =>
        client.Server!.Authenticate(new RelayLogin { Authorization = authorization, HostName = hostName });

    // a witness's message joins the client's queue behind anything already on its way, a kick included
    private async Task AssertNotKicked(TestStyxClient client, Guid network, string hostName)
    {
        await using var witness = await Witness(network);
        await witness.Server!.Send([hostName], [0]);
        await client.WaitForReceive();
        Assert.That(client.KickReason, Is.Null);
    }

    private const string WitnessName = "witness";

    private async Task<TestStyxClient> Witness(Guid network)
    {
        var witness = new TestStyxClient();
        await witness.Connect(Factory, StyxTestServer.MintToken(network).ToString(), WitnessName);
        return witness;
    }
}
