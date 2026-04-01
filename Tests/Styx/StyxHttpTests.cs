using System.Net;
using System.Net.Http.Json;
using Hydra.Config;
using System.Text;
using System.Text.Json;
using Cathedral.Config;
using Cathedral.Utils;
using Common;
using Tests.Setup;

namespace Tests.Styx;

[TestFixture]
public class StyxHttpTests
{
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<global::Styx.Program>? _factory;
    private HttpClient? _http;

    [SetUp]
    public void SetUp()
    {
        _factory = StyxTestServer.Create();
        _http = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [TearDown]
    public async Task TearDown()
    {
        _http?.Dispose();
        if (_factory != null)
            await _factory.DisposeAsync();
    }

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
    public async Task NetworkConfig_AuthorizationDecryptsToGuid()
    {
        var response = await _http!.PostAsJsonAsync("/api/network-config", new { Password = StyxTestServer.TestPassword });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var authorization = body.GetProperty("authorization").GetString()!;

        // the authorization blob must decrypt back to a valid Guid with the same password
        var networkId = await new SimpleAes(StyxTestServer.TestPassword).DecryptBase64<Guid>(authorization, true, CancellationToken.None);
        Assert.That(networkId, Is.Not.EqualTo(Guid.Empty));
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

        // each call generates a fresh Guid, so the encrypted blobs must differ
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
        var key = StyxTestServer.GenerateEncryptionKey();
        var styxServer = _factory!.Server.BaseAddress.ToString().TrimEnd('/');
        var config = new NetworkConfig(styxServer, key, authorization);
        var configBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(config, SaneJson.Options)));

        await using var client = new HydraTestClient(_factory!, new Hydra.Config.HydraConfig { Mode = Mode.Master, NetworkConfig = configBase64, HostName = "api-test" });
        await client.StartAsync(CancellationToken.None);
        await client.WaitForReady();

        Assert.That(client.IsConnected, Is.True);
    }
}
