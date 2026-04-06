using Hydra.Config;

namespace Hydra.Platform.MacOs;

// reads macOS network state from MacNetworkState, which is populated by MacShieldProcess
// (the hydra-shield Swift binary handles detection via NWPathMonitor + CoreWLAN).
internal sealed class MacNetworkDetector(MacNetworkState? networkState = null) : INetworkDetector
{
    public Task<List<NetworkState>> GetActiveNetworks(CancellationToken cancel = default)
    {
        var results = new List<NetworkState>();
        if (!string.IsNullOrEmpty(networkState?.Ssid))
            results.Add(new NetworkState(ConfigCondition.Ssid, networkState.Ssid));
        if (networkState?.Wired == true)
            results.Add(new NetworkState(ConfigCondition.Wired, null));
        return Task.FromResult(results);
    }
}
