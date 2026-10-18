using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cathedral.Styx;
using Common;
using Tests.Setup;

namespace Tests.Styx;

[TestFixture]
public class StyxHttpTests : StyxFixtureBase
{
    private HttpClient? _http;

    [OneTimeSetUp]
    public void CreateHttpClient()
    {
        _http = Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [OneTimeTearDown]
    public void DisposeHttpClient() => _http?.Dispose();

    [Test]
    public async Task Root_Returns200WithHtmlContent()
    {
        var response = await _http!.GetAsync("/");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var content = await response.Content.ReadAsStringAsync();
        Assert.That(content, Does.Contain("<!DOCTYPE html>").Or.Contain("<html"));
    }

    [Test]
    public async Task IndexHtml_Returns200WithHtmlContent()
    {
        var response = await _http!.GetAsync("/index.html");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var content = await response.Content.ReadAsStringAsync();
        Assert.That(content, Does.Contain("<!DOCTYPE html>").Or.Contain("<html"));
    }

    [Test]
    public async Task NetworkConfig_WrongPassword_Returns401()
    {
        var response = await _http!.PostAsJsonAsync("/api/network-config", new { Password = "wrong-password" });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task NetworkConfig_CorrectPassword_ReturnsAuthorization()
    {
        var response = await _http!.PostAsJsonAsync("/api/network-config", new { Password = StyxTestServer.TestPassword });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var authorization = body.GetProperty("authorization").GetString();
        Assert.That(authorization, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task NetworkConfig_IssuesAV2RootForAFreshVersion8Network()
    {
        var response = await _http!.PostAsJsonAsync("/api/network-config", new { Password = StyxTestServer.TestPassword });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var token = StyxToken.Parse(body.GetProperty("authorization").GetString()!);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(token.Verify(StyxTestServer.TestKey), Is.True);
            Assert.That(token.Network.Version, Is.EqualTo(8));
            Assert.That(token.Caveats, Is.Empty);
        }
    }

    [Test]
    public async Task NetworkConfig_TwoCalls_ReturnDifferentAuthorizations()
    {
        var resp1 = await _http!.PostAsJsonAsync("/api/network-config", new { Password = StyxTestServer.TestPassword });
        var resp2 = await _http!.PostAsJsonAsync("/api/network-config", new { Password = StyxTestServer.TestPassword });

        var body1 = await resp1.Content.ReadFromJsonAsync<JsonElement>();
        var body2 = await resp2.Content.ReadFromJsonAsync<JsonElement>();

        var auth1 = body1.GetProperty("authorization").GetString();
        var auth2 = body2.GetProperty("authorization").GetString();

        // each call mints a fresh network, so the tokens must differ
        Assert.That(auth1, Is.Not.EqualTo(auth2));
    }

    [Test]
    public async Task NetworkConfig_AuthorizationCanBeUsedToAuthenticate()
    {
        // end-to-end: get an auth blob from the API, use it in a real Hydra relay connection
        var response = await _http!.PostAsJsonAsync("/api/network-config", new { Password = StyxTestServer.TestPassword });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var authorization = body.GetProperty("authorization").GetString()!;

        // build a NetworkConfig base64 string using the API-issued authorization
        var configBase64 = new NetworkConfig(StyxTestServer.ServerUrl(Factory), NetworkConfig.NewEncryptionKey(), authorization).ToBlob();

        await using var client = await HydraTestClient.Started(HydraTestClient.Master(Factory, "api-test", configBase64));

        Assert.That(client.IsConnected, Is.True);
    }

    [Test]
    public async Task Status_InvalidAuthorization_Returns401()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer not-valid-base64!!!");
        var response = await _http!.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Status_ValidAuthorization_NoPeers_ReturnsEmptyArray()
    {
        var auth = await StyxTestServer.GenerateAuthorization(Guid.NewGuid());

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {auth}");
        var response = await _http!.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var peers = body.GetProperty("peers").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.That(peers, Is.Empty);
    }

    [Test]
    public async Task Status_ValidAuthorization_WithConnectedPeers_ReturnsPeerNames()
    {
        var networkId = Guid.NewGuid();
        var auth = await StyxTestServer.GenerateAuthorization(networkId);

        await using var clientA = new TestStyxClient();
        await using var clientB = new TestStyxClient();
        await clientA.Connect(Factory, auth, "machine-alpha");
        await clientB.Connect(Factory, auth, "machine-beta");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {auth}");
        var response = await _http!.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var peers = body.GetProperty("peers").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.That(peers, Is.EquivalentTo(["machine-alpha", "machine-beta"]));
    }
}
