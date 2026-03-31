using System.Text;
using System.Text.Json;
using Cathedral.Config;
using Cathedral.Utils;
using Common;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Tests.Setup;

public static class StyxTestServer
{
    public const string TestPassword = "test-relay-password-hydra";

    public static WebApplicationFactory<global::Styx.Program> Create(string password = TestPassword)
    {
        // must be set before the factory initializes the host
        Environment.SetEnvironmentVariable("RELAY_PASSWORD", password);

        return new WebApplicationFactory<global::Styx.Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services => TestLog.ConfigureFileLogging(services));
            });
    }

    // generates a valid authorization blob for the given networkId, signed with the given password
    public static async Task<string> GenerateAuthorization(Guid networkId, string password = TestPassword)
        => await new SimpleAes(password).EncryptBase64(networkId, CancellationToken.None);

    // builds the base64-encoded NetworkConfig string that HydraConfig.NetworkConfig expects
    public static async Task<string> BuildNetworkConfig(
        WebApplicationFactory<global::Styx.Program> factory,
        Guid networkId,
        string? encryptionKey = null,
        string password = TestPassword)
    {
        var key = encryptionKey ?? GenerateEncryptionKey();
        var authorization = await GenerateAuthorization(networkId, password);
        var styxServer = factory.Server.BaseAddress.ToString().TrimEnd('/');
        var config = new NetworkConfig(styxServer, key, authorization);
        var json = JsonSerializer.Serialize(config, SaneJson.Options);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    // 128-char alphanumeric key, matching what the web UI generates
    public static string GenerateEncryptionKey()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        return new string(Random.Shared.GetItems(chars.AsSpan(), 128));
    }
}
