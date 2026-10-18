using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cathedral.Config;
using Cathedral.Utils;

namespace Common;

// Embedded: the relay is a Hydra's embedded one, whose self-signed cert proves itself with EncryptionKey, the relay password
public record NetworkConfig(
    string StyxServer,
    string EncryptionKey,
    string Authorization,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Embedded = false)
{
    // the one network every peer of an embedded relay shares
    public static readonly Guid EmbeddedNetworkId = new("1337b007-dead-beef-cafe-000123456789");

    private const string EncryptionKeyAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    // 128 alphanumerics, the form the web UI generates
    public static string NewEncryptionKey() => RandomNumberGenerator.GetString(EncryptionKeyAlphabet, 128);

    // a v1 authorization: the network id encrypted under the relay password
    public static async ValueTask<string> V1Authorization(string password, Guid network, CancellationToken cancel = default) =>
        await new SimpleAes(password).EncryptBase64(network, cancel);

    public static NetworkConfig Parse(string base64)
    {
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        return JsonSerializer.Deserialize<NetworkConfig>(json, SaneJson.Options)
            ?? throw new InvalidOperationException("Failed to deserialize network config");
    }

    // derives a network config blob for connecting to an embedded Styx server — no pre-shared blob needed,
    // any machine with the password can independently compute a valid authorization token
    public static async ValueTask<string> ComputeEmbeddedBlob(string server, string password, CancellationToken cancel = default)
    {
        var authorization = await V1Authorization(password, EmbeddedNetworkId, cancel);
        return new NetworkConfig(server, password, authorization, Embedded: true).ToBlob();
    }

    public string ToBlob() => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, SaneJson.Options)));
}
