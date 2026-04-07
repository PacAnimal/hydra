namespace Hydra.Platform.MacOs;

// reads macOS network state from MacNetworkState, which is populated by MacShieldProcess
// (the hydra-shield Swift binary handles SSID detection via CoreWLAN).
internal sealed class MacNetworkDetector(MacNetworkState? networkState = null) : INetworkDetector
{
    public Task<List<string>> GetActiveSsids(CancellationToken cancel = default)
    {
        var results = new List<string>();
        if (!string.IsNullOrEmpty(networkState?.Ssid))
            results.Add(networkState.Ssid);
        return Task.FromResult(results);
    }
}
