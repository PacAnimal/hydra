namespace Hydra.Platform.MacOs;

// reads macOS network state from MacNetworkState, which is populated by MacShieldProcess
// (the hydra-shield Swift binary handles SSID detection via CoreWLAN).
internal sealed class MacNetworkDetector(MacNetworkState? networkState = null) : INetworkDetector
{
    public Task<List<string>?> GetActiveSsids(CancellationToken cancel = default)
    {
        // no state source wired up → detection unavailable (unknown), not "no wifi"
        if (networkState == null) return Task.FromResult<List<string>?>(null);
        List<string> results = string.IsNullOrEmpty(networkState.Ssid) ? [] : [networkState.Ssid];
        return Task.FromResult<List<string>?>(results);
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
