using Cathedral.Styx;
using Common;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Styx;

namespace Tests.Setup;

public static class StyxTestServer
{
    public const string TestPassword = "test-relay-password-hydra";

    /// <summary>The test servers' v2 key. Not Styx.md's published one, which a relay refuses to start with.</summary>
    public static readonly byte[] TestKey = [.. Enumerable.Range(0, 64).Select(i => (byte)(0x80 + i))];

    public static WebApplicationFactory<global::Styx.Program> Create(Action<IServiceCollection>? configure = null)
    {
        // must be set before the factory initializes the host, and the same for every server, since it is process-wide;
        // anything else a test wants differs in code, by replacing StyxAuth through `configure`
        Environment.SetEnvironmentVariable(Constants.RelayPasswordEnvVar, TestPassword);
        Environment.SetEnvironmentVariable(Constants.RelaySigningKeyEnvVar, Convert.ToBase64String(TestKey));
        Environment.SetEnvironmentVariable(Constants.RelaySigningPassphraseEnvVar, null);
        Environment.SetEnvironmentVariable(Constants.RevokedNetworksEnvVar, null);
        Environment.SetEnvironmentVariable(Constants.RevokedIdsEnvVar, null);
        Environment.SetEnvironmentVariable(Constants.ParallelInvocationsEnvVar, null);

        return new WebApplicationFactory<global::Styx.Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    TestLog.ConfigureFileLogging(services);
                    services.AddSingleton(new ResponseThrottle(new InstantTimeProvider()));
                    configure?.Invoke(services);
                });
            });
    }

    /// <summary>A sender and a receiver, both masters on a fresh network, both authenticated.</summary>
    public static async Task<ClientPair> ConnectedPair(
        WebApplicationFactory<global::Styx.Program> factory, string sender = "sender", string receiver = "receiver")
    {
        var cfg = await BuildNetworkConfig(factory, Guid.NewGuid());
        return await ClientPair.Start(HydraTestClient.Master(factory, sender, cfg), HydraTestClient.Master(factory, receiver, cfg));
    }

    /// <summary>A v2 token from the test key, for a fresh network unless one is given.</summary>
    public static StyxToken MintToken(Guid? network = null, params IEnumerable<StyxCaveat> caveats) =>
        StyxToken.Mint(TestKey, network ?? StyxToken.NewNetworkId(), caveats);

    // a v1 authorization for the given network, under the test servers' password unless another is given
    public static async Task<string> GenerateAuthorization(Guid networkId, string password = TestPassword) =>
        await NetworkConfig.V1Authorization(password, networkId);

    // builds the base64-encoded NetworkConfig string that HydraConfig.NetworkConfig expects
    public static async Task<string> BuildNetworkConfig(
        WebApplicationFactory<global::Styx.Program> factory,
        Guid networkId,
        string? encryptionKey = null,
        string password = TestPassword)
    {
        var authorization = await GenerateAuthorization(networkId, password);
        return new NetworkConfig(ServerUrl(factory), encryptionKey ?? NetworkConfig.NewEncryptionKey(), authorization).ToBlob();
    }

    // the test server's address, as a blob's StyxServer spells it
    public static string ServerUrl(WebApplicationFactory<global::Styx.Program> factory) => factory.Server.BaseAddress.ToString().TrimEnd('/');
}
