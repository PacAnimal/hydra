using Common.DTO;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Tests.Setup;

namespace Tests.Styx;

// a third-party client picks its own member-name casing; both hub protocols must accept either.
[TestFixture]
public class StyxLoginCasingTests
{
    private static WebApplicationFactory<global::Styx.Program>? _factory;

    [OneTimeSetUp]
    public static void OneTimeSetUp()
    {
        _factory = StyxTestServer.Create();
        _ = _factory.Server;
    }

    [OneTimeTearDown]
    public static async Task OneTimeTearDown()
    {
        if (_factory != null)
            await _factory.DisposeAsync();
    }

    public enum Protocol { Json, MessagePack }

    // invokes Authenticate with a hand-built argument map, bypassing the typed client's own casing
    private static async Task<RelayLoginResponse> Authenticate(Protocol protocol, Dictionary<string, string> login)
    {
        var builder = new HubConnectionBuilder()
            .WithUrl($"{_factory!.Server.BaseAddress}relay",
                options => options.HttpMessageHandlerFactory = _ => _factory!.Server.CreateHandler());

        if (protocol == Protocol.MessagePack) builder.AddMessagePackProtocol();

        await using var hub = builder.Build();
        await hub.StartAsync();
        return await hub.InvokeAsync<RelayLoginResponse>("Authenticate", login);
    }

    [TestCase(Protocol.Json, "authorization", "hostName", TestName = "CamelCase over JSON")]
    [TestCase(Protocol.Json, "Authorization", "HostName", TestName = "PascalCase over JSON")]
    [TestCase(Protocol.Json, "AUTHORIZATION", "hostname", TestName = "Mixed case over JSON")]
    [TestCase(Protocol.MessagePack, "authorization", "hostName", TestName = "CamelCase over MessagePack")]
    [TestCase(Protocol.MessagePack, "Authorization", "HostName", TestName = "PascalCase over MessagePack")]
    [TestCase(Protocol.MessagePack, "AUTHORIZATION", "hostname", TestName = "Mixed case over MessagePack")]
    public async Task Authenticate_AcceptsAnyMemberCasing(Protocol protocol, string authKey, string hostKey)
    {
        var authorization = await StyxTestServer.GenerateAuthorization(Guid.NewGuid());
        var response = await Authenticate(protocol, new Dictionary<string, string>
        {
            [authKey] = authorization,
            [hostKey] = $"casing-{protocol}-{authKey}",
        });

        Assert.That(response.Authenticated, Is.True);
    }

    [TestCase(Protocol.Json)]
    [TestCase(Protocol.MessagePack)]
    public async Task Authenticate_MissingHostName_IsRefusedCleanly(Protocol protocol)
    {
        var authorization = await StyxTestServer.GenerateAuthorization(Guid.NewGuid());
        var response = await Authenticate(protocol, new Dictionary<string, string> { ["authorization"] = authorization });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Authenticated, Is.False);
            Assert.That(response.Message, Does.Contain("required"));
        }
    }

    [TestCase(Protocol.Json)]
    [TestCase(Protocol.MessagePack)]
    public async Task Authenticate_MissingAuthorization_IsRefusedCleanly(Protocol protocol)
    {
        var response = await Authenticate(protocol, new Dictionary<string, string> { ["hostName"] = "no-token" });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Authenticated, Is.False);
            Assert.That(response.Message, Does.Contain("required"));
        }
    }
}
