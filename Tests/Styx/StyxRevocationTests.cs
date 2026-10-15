using System.Net;
using System.Net.Http.Json;
using Cathedral.Styx;
using Cathedral.Utils;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Styx;
using Styx.Services;
using Tests.Setup;

namespace Tests.Styx;

/// <summary>What a relay's configuration refuses: revoked networks and ids, and network creation it cannot guard.</summary>
[TestFixture]
public class StyxRevocationTests
{
    private static readonly Guid RevokedV2Network = StyxToken.NewNetworkId();
    private static readonly Guid RevokedV1Network = Guid.NewGuid();

    [Test]
    public async Task ARevokedNetwork_IsRefused_V1AndV2()
    {
        await using var factory = Relay(Auth() with { RevokedNetworks = new HashSet<Guid> { RevokedV2Network, RevokedV1Network } });
        await using var v2 = new TestStyxClient();
        await using var v1 = new TestStyxClient();
        await using var unrevoked = new TestStyxClient();
        using (Assert.EnterMultipleScope())
        {
            Assert.That((await v2.Connect(factory, StyxTestServer.MintToken(RevokedV2Network).ToString(), "a")).Authenticated, Is.False);
            Assert.That((await v1.Connect(factory, await StyxTestServer.GenerateAuthorization(RevokedV1Network), "b")).Authenticated, Is.False);
            Assert.That((await unrevoked.Connect(factory, StyxTestServer.MintToken().ToString(), "c")).Authenticated, Is.True);
        }
    }

    [Test]
    public async Task ARevokedId_IsRefused_AndSoIsEveryTokenNarrowedFromIt()
    {
        await using var factory = Relay(Auth() with { RevokedIds = new HashSet<string> { "leaked" } });
        var leaked = StyxTestServer.MintToken(null, StyxCaveat.Id("leaked"));
        await using var holder = new TestStyxClient();
        await using var descendant = new TestStyxClient();
        await using var other = new TestStyxClient();
        using (Assert.EnterMultipleScope())
        {
            Assert.That((await holder.Connect(factory, leaked.ToString(), "a")).Authenticated, Is.False);
            Assert.That((await descendant.Connect(factory, leaked.Narrow(StyxCaveat.Sub("x")).ToString(), "b")).Authenticated, Is.False);
            Assert.That((await other.Connect(factory, StyxTestServer.MintToken(null, StyxCaveat.Id("Leaked")).ToString(), "c")).Authenticated, Is.True,
                "ids compare ordinally");
        }
    }

    [Test]
    public async Task WithoutAPassword_NoV1TokenIsAccepted()
    {
        await using var factory = Relay(Auth() with { V1Password = null });
        await using var client = new TestStyxClient();
        var response = await client.Connect(factory, await StyxTestServer.GenerateAuthorization(Guid.NewGuid()), "a");
        Assert.That(response.Authenticated, Is.False);
    }

    [Test]
    public async Task ARotatedPassword_RefusesEveryOldV1Token()
    {
        await using var factory = Relay(Auth() with { V1Password = "the-new-long-random-password" });
        await using var client = new TestStyxClient();
        var response = await client.Connect(factory, await StyxTestServer.GenerateAuthorization(Guid.NewGuid()), "a");
        Assert.That(response.Authenticated, Is.False);
    }

    [Test]
    public async Task NetworkCreation_IsNotFound_WithoutAKey()
    {
        await using var factory = Relay(Auth() with { Keys = [] });
        using var http = factory.CreateClient();
        var response = await http.PostAsJsonAsync("/api/network-config", new { Password = StyxTestServer.TestPassword });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task NetworkCreation_IsNotFound_WithoutAnAdminSecret()
    {
        await using var factory = Relay(Auth() with { AdminPassword = null });
        using var http = factory.CreateClient();
        var response = await http.PostAsJsonAsync("/api/network-config", new { Password = "" });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task NetworkCreation_ChecksTheAdminSecret_NotTheV1Password()
    {
        await using var factory = Relay(Auth() with { AdminPassword = "admin-secret" });
        using var http = factory.CreateClient();
        var withPassword = await http.PostAsJsonAsync("/api/network-config", new { Password = StyxTestServer.TestPassword });
        var withSecret = await http.PostAsJsonAsync("/api/network-config", new { Password = "admin-secret" });
        using (Assert.EnterMultipleScope())
        {
            Assert.That(withPassword.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(withSecret.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }
    }

    [Test]
    public async Task WrongAdminSecrets_CountTowardTheAddressLimit()
    {
        await using var factory = Relay(Auth() with { AdminPassword = "admin-secret" });
        using var http = factory.CreateClient();
        for (var i = 0; i < FailureLimiter.MaxFailures; i++)
            await http.PostAsJsonAsync("/api/network-config", new { Password = "guess" });
        var right = await http.PostAsJsonAsync("/api/network-config", new { Password = "admin-secret" });
        Assert.That(right.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), "refused without checking, until the minute passes");
    }

    [Test]
    public async Task AV1TokenForAVersion8Network_IsRefusedEvenWhenTheV1PathIsOn()
    {
        await using var factory = Relay(Auth());
        await using var client = new TestStyxClient();
        var v1 = await new SimpleAes(StyxTestServer.TestPassword).EncryptBase64(StyxToken.NewNetworkId(), CancellationToken.None);
        Assert.That((await client.Connect(factory, v1, "a")).Authenticated, Is.False);
    }

    [Test]
    public async Task ABlockedAddress_IsRefusedV1Guesses_ButNeverV2Logins()
    {
        await using var factory = Relay(Auth());
        var limiter = factory.Services.GetRequiredService<FailureLimiter>();
        foreach (var address in new[] { ClientRegistry.Unknown, "127.0.0.1", "::1" })
            for (var i = 0; i < FailureLimiter.MaxFailures; i++) limiter.RecordFailure(address);

        await using var v2 = new TestStyxClient();
        await using var v1 = new TestStyxClient();
        using (Assert.EnterMultipleScope())
        {
            Assert.That((await v2.Connect(factory, StyxTestServer.MintToken().ToString(), "a")).Authenticated, Is.True);
            Assert.That((await v1.Connect(factory, await StyxTestServer.GenerateAuthorization(Guid.NewGuid()), "b")).Authenticated, Is.False);
        }
    }

    [Test]
    public void StyxAuth_LeavesItsSecretsOutOfToString()
    {
        var text = (Auth() with { AdminPassword = "admin-secret" }).ToString();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(text, Does.Not.Contain(StyxTestServer.TestPassword));
            Assert.That(text, Does.Not.Contain("admin-secret"));
            Assert.That(text, Does.Not.Contain(Convert.ToBase64String(StyxTestServer.TestKey)));
        }
    }

    private static StyxAuth Auth() =>
        new([StyxTestServer.TestKey], StyxTestServer.TestPassword, StyxTestServer.TestPassword, new HashSet<Guid>(), new HashSet<string>());

    private static WebApplicationFactory<global::Styx.Program> Relay(StyxAuth auth) =>
        StyxTestServer.Create(configure: services => services.AddSingleton(auth));
}
