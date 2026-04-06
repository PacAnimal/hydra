using Hydra.Config;

namespace Hydra.Platform;

public interface INetworkDetector
{
    Task<List<NetworkState>> GetActiveNetworks(CancellationToken cancel = default);
}

// represents one active network connection
public record NetworkState(ConfigCondition Type, string? Ssid)
{
    public override string ToString() => Type == ConfigCondition.Ssid ? $"WiFi ({Ssid})" : "Wired";
}
