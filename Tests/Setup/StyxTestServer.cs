using System.Text;
using System.Text.Json;
using Cathedral.Config;
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

    public static WebApplicationFactory<global::Styx.Program> Create(string password = TestPassword, Action<IServiceCollection>? configure = null)
    {
        // must be set before the factory initializes the host. process-wide, so safe only while fixtures run
        // one at a time — every caller uses the default today
        Environment.SetEnvironmentVariable("RELAY_PASSWORD", password);

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
