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
        var authorization = await new SimpleAes(password).EncryptBase64(EmbeddedRelayConstants.NetworkId, cancel);
        return new NetworkConfig(server, password, authorization, Embedded: true).ToBlob();
    }

    public string ToBlob() => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, SaneJson.Options)));
}
