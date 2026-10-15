using System.Text;
using System.Text.Json;
using Cathedral.Config;
using Cathedral.Styx;
using Cathedral.Utils;
using Common;
using Hydra.Relay;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Styx;

namespace Tests.Setup;

public static class StyxTestServer
{
    public const string TestPassword = "test-relay-password-hydra";

    /// <summary>The test servers' v2 key. Not Styx.md's published one, which a relay refuses to start with.</summary>
    public static readonly byte[] TestKey = [.. Enumerable.Range(0, 64).Select(i => (byte)(0x80 + i))];

    public static WebApplicationFactory<global::Styx.Program> Create(string password = TestPassword, Action<IServiceCollection>? configure = null)
    {
        // must be set before the factory initializes the host. process-wide, so safe only while fixtures run
        // one at a time — every caller uses the default today. Anything else a test wants differs in code,
        // by replacing StyxAuth through `configure`.
        Environment.SetEnvironmentVariable(global::Styx.Constants.RelayPasswordEnvVar, password);
        Environment.SetEnvironmentVariable(global::Styx.Constants.RelaySigningKeyEnvVar, Convert.ToBase64String(TestKey));
        Environment.SetEnvironmentVariable(global::Styx.Constants.RelaySigningPassphraseEnvVar, null);
        // a retired name left in the shell would make every relay here refuse to start
        foreach (var (name, _) in StyxEnvironment.RetiredNames) Environment.SetEnvironmentVariable(name, null);
        Environment.SetEnvironmentVariable(global::Styx.Constants.RevokedNetworksEnvVar, null);
        Environment.SetEnvironmentVariable(global::Styx.Constants.RevokedIdsEnvVar, null);
        Environment.SetEnvironmentVariable(global::Styx.Constants.ParallelInvocationsEnvVar, null);

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

    /// <summary>
    /// A sender and a receiver, both masters on a fresh network, both authenticated.
    ///
    /// <para><c>peerTakesBundles</c> is what the sender believes the receiver advertised; null leaves it
    /// unknown. FALSE is the un-upgraded peer, and it is not a corner case — it is every slave in the field
    /// until it is updated.</para>
    /// </summary>
    public static async Task<ClientPair> ConnectedPair(
        WebApplicationFactory<global::Styx.Program> factory, string sender = "sender", string receiver = "receiver", bool? peerTakesBundles = null)
    {
        var cfg = await BuildNetworkConfig(factory, Guid.NewGuid());
        var senderClient = HydraTestClient.Master(factory, sender, cfg);
        if (peerTakesBundles is { } bundles)
            senderClient.World.SetPeerCapabilities(receiver, PeerCapabilities.Parse(bundles ? PeerCapabilities.Advertise() : null));

        return await ClientPair.Start(senderClient, HydraTestClient.Master(factory, receiver, cfg));
    }

    /// <summary>A v2 token from the test key, for a fresh network unless one is given.</summary>
    public static StyxToken MintToken(Guid? network = null, params IEnumerable<StyxCaveat> caveats) =>
        StyxToken.Mint(TestKey, network ?? StyxToken.NewNetworkId(), caveats);

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
