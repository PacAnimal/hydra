using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cathedral.Styx;
using Styx;
using Tests.Setup;

namespace Tests.Styx;

/// <summary>v2 tokens at a running relay: who they let in, as what name, and where.</summary>
[TestFixture]
public class StyxV2Tests : StyxFixtureBase
{
    [Test]
    public async Task ARootToken_Authenticates()
    {
        await using var client = new TestStyxClient();
        var response = await client.Connect(Factory, StyxTestServer.MintToken().ToString(), "alpha");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Authenticated, Is.True);
            Assert.That(await client.WaitForPeers(), Is.Empty);
        }
    }

    [Test]
    public async Task ATokenUnderAnotherKey_IsRefusedLikeAnyInvalidOne()
    {
        var forged = StyxToken.Mint(StyxToken.NewKey(), StyxToken.NewNetworkId()).ToString();
        await using var client = new TestStyxClient();
        var response = await client.Connect(Factory, forged, "alpha");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Authenticated, Is.False);
            Assert.That(response.Message, Is.EqualTo(RelayReasons.InvalidAuthorization));
        }
    }

    [Test]
    public async Task AnExpiredToken_SaysSo()
    {
        var expired = StyxTestServer.MintToken(null, StyxCaveat.Expires(DateTimeOffset.UnixEpoch.AddSeconds(1))).ToString();
        await using var client = new TestStyxClient();
        var response = await client.Connect(Factory, expired, "alpha");
        Assert.That(response.Message, Is.EqualTo(RelayReasons.AuthorizationExpired));
    }

    [Test]
    public async Task AHostCaveat_AdmitsOnlyThatName_InAnyCase_ReportedLowercase()
    {
        var token = StyxTestServer.MintToken(null, StyxCaveat.Host("player-1")).ToString();
        await using var allowed = new TestStyxClient();
        await using var other = new TestStyxClient();
        await using var watcher = new TestStyxClient();

        var refused = await other.Connect(Factory, token, "player-2");
        var accepted = await allowed.Connect(Factory, token, "PLAYER-1");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(refused.Authenticated, Is.False);
            Assert.That(refused.Message, Is.EqualTo(RelayReasons.HostNameNotAllowed));
            Assert.That(accepted.Authenticated, Is.True);
        }

        var root = StyxToken.Parse(token).Network;
        await watcher.Connect(Factory, StyxTestServer.MintToken(root).ToString(), "watcher");
        Assert.That(await watcher.WaitForPeers(["player-1"]), Is.EqualTo(["player-1"]));
    }

    [Test]
    public async Task HostNotAndHostPrefix_AreEnforced()
    {
        var token = StyxTestServer.MintToken(null, StyxCaveat.HostPrefix("att-"), StyxCaveat.HostNot("att-server")).ToString();
        await using var server = new TestStyxClient();
        await using var outsider = new TestStyxClient();
        await using var laptop = new TestStyxClient();
        using (Assert.EnterMultipleScope())
        {
            Assert.That((await server.Connect(Factory, token, "ATT-Server")).Message, Is.EqualTo(RelayReasons.HostNameNotAllowed));
            Assert.That((await outsider.Connect(Factory, token, "other")).Message, Is.EqualTo(RelayReasons.HostNameNotAllowed));
            Assert.That((await laptop.Connect(Factory, token, "att-laptop")).Authenticated, Is.True);
        }
    }

    [Test]
    public async Task UnderAHostnameCaveat_ALookalikeNameIsRefused()
    {
        var token = StyxTestServer.MintToken(null, StyxCaveat.HostNot("kiosk")).ToString();
        await using var kelvin = new TestStyxClient();
        await using var padded = new TestStyxClient();
        using (Assert.EnterMultipleScope())
        {
            Assert.That((await kelvin.Connect(Factory, token, "Kiosk")).Message, Is.EqualTo(RelayReasons.HostNameNotAllowed));
            Assert.That((await padded.Connect(Factory, token, "kiosk ")).Message, Is.EqualTo(RelayReasons.HostNameNotAllowed));
        }
    }

    [Test]
    public async Task WithoutCaveats_TwoNamesThatLowercaseAlike_ShareOneSlot()
    {
        var token = StyxTestServer.MintToken().ToString();
        await using var real = new TestStyxClient();
        await using var lookalike = new TestStyxClient();
        await real.Connect(Factory, token, "kiosk");
        await lookalike.Connect(Factory, token, "Kiosk");
        Assert.That(await real.WaitForKick(), Is.EqualTo(RelayReasons.KickedDuplicateHostname));
    }

    [Test]
    public async Task SubNetworks_CannotSeeOrAddressEachOther()
    {
        var network = StyxToken.NewNetworkId();
        await using var roomA = new TestStyxClient();
        await using var roomA2 = new TestStyxClient();
        await using var roomB = new TestStyxClient();
        await using var roomB2 = new TestStyxClient();
        var roomBToken = StyxTestServer.MintToken(network, StyxCaveat.Sub("game"), StyxCaveat.Sub("b")).ToString();
        await roomB.Connect(Factory, roomBToken, "bee");
        await roomB2.Connect(Factory, roomBToken, "bee2");
        await roomA.Connect(Factory, StyxTestServer.MintToken(network, StyxCaveat.Sub("game"), StyxCaveat.Sub("a")).ToString(), "ay");
        await roomA2.Connect(Factory, StyxTestServer.MintToken(network, StyxCaveat.Sub("game"), StyxCaveat.Sub("a")).ToString(), "ay2");

        Assert.That(await roomA.WaitForPeers(["ay2"]), Is.EqualTo(["ay2"]));
        await roomA.Server!.Send(["bee", "ay2"], [1, 2, 3]);
        // a send has its place in every target's queue once it returns, so this sentinel lands behind any leak
        await roomB2.Server!.Send(["bee"], [9]);
        var (source, _, _) = await roomA2.WaitForReceive();
        var (bSource, _, _) = await roomB.WaitForReceive();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source, Is.EqualTo("ay"));
            Assert.That(bSource, Is.EqualTo("bee2"), "room b received room a's send");
        }
    }

    [Test]
    public async Task APathElementWithADot_CannotReachTheNestedPathItLooksLike()
    {
        // `[a.b]` would equal `[a, b]` under a '.'-joined path; '.' is outside the grammar, so it can't be minted
        Assert.Throws<StyxTokenException>(() => StyxCaveat.Sub("a.b"));
        var network = StyxToken.NewNetworkId();
        await using var nested = new TestStyxClient();
        await using var cased = new TestStyxClient();
        await nested.Connect(Factory, StyxTestServer.MintToken(network, StyxCaveat.Sub("a"), StyxCaveat.Sub("b")).ToString(), "nested");
        await cased.Connect(Factory, StyxTestServer.MintToken(network, StyxCaveat.Sub("A"), StyxCaveat.Sub("b")).ToString(), "cased");
        await nested.Server!.Send(["cased"], [9]);
        await cased.Server!.Send(["cased"], [7]);
        Assert.That((await cased.WaitForReceive()).Payload, Is.EqualTo(new byte[] { 7 }), "paths compare case-sensitively");
    }

    [Test]
    public async Task AV1TokenForAVersion8Network_IsRefused()
    {
        var v1 = await StyxTestServer.GenerateAuthorization(StyxToken.NewNetworkId());
        await using var client = new TestStyxClient();
        Assert.That((await client.Connect(Factory, v1, "alpha")).Message, Is.EqualTo(RelayReasons.InvalidAuthorization));
    }

    [Test]
    public async Task MovingToAnotherPath_TellsTheOldOneItLeft()
    {
        var network = StyxToken.NewNetworkId();
        var lobby = StyxTestServer.MintToken(network).ToString();
        await using var stayer = new TestStyxClient();
        await using var mover = new TestStyxClient();
        await stayer.Connect(Factory, lobby, "stayer");
        await mover.Connect(Factory, lobby, "mover");
        Assert.That(await stayer.WaitForPeers(["mover"]), Is.EqualTo(["mover"]));

        var moved = await mover.Server!.Authenticate(new Common.DTO.RelayLogin
        {
            Authorization = StyxTestServer.MintToken(network, StyxCaveat.Sub("room")).ToString(),
            HostName = "mover",
        });
        using (Assert.EnterMultipleScope())
        {
            Assert.That(moved.Authenticated, Is.True);
            Assert.That(await stayer.WaitForPeers([]), Is.Empty);
        }
    }

    [Test]
    public async Task Status_WithAV2Token_ListsThePeersOnItsPath()
    {
        var network = StyxToken.NewNetworkId();
        var room = StyxTestServer.MintToken(network, StyxCaveat.Sub("room")).ToString();
        await using var inRoom = new TestStyxClient();
        await using var atRoot = new TestStyxClient();
        await inRoom.Connect(Factory, room, "in-room");
        await atRoot.Connect(Factory, StyxTestServer.MintToken(network).ToString(), "at-root");

        using var http = Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {room}");
        var response = await http.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body.GetProperty("peers").EnumerateArray().Select(e => e.GetString()), Is.EqualTo(["in-room"]));
        }
    }
}
