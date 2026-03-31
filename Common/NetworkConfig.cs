using System.Text;
using System.Text.Json;
using Cathedral.Config;

namespace Common;

public record NetworkConfig(string StyxServer, string EncryptionKey, string Authorization)
{
    public string ServerUrl => StyxServer;

    public static NetworkConfig Parse(string base64)
    {
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        return JsonSerializer.Deserialize<NetworkConfig>(json, SaneJson.Options)
            ?? throw new InvalidOperationException("Failed to deserialize network config");
    }
}
