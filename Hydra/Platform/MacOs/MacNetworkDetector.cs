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

    public Task<bool?> GetIsPluggedIn(CancellationToken cancel = default) =>
        Task.FromResult(QueryIsPluggedIn());

    private static bool? QueryIsPluggedIn()
    {
        var snapshot = NativeMethods.IOPSCopyPowerSourcesInfo();
        if (snapshot == nint.Zero) return null;
        try
        {
            var typeRef = NativeMethods.IOPSGetProvidingPowerSourceType(snapshot);
            if (typeRef == nint.Zero) return null;
            return NativeMethods.CfStringToManaged(typeRef) == "AC Power";
        }
        finally
        {
            NativeMethods.CFRelease(snapshot);
        }
    }
}
