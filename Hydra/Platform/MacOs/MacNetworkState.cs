namespace Hydra.Platform.MacOs;

// shared state populated by MacShieldProcess (from shield stdout) and consumed by MacNetworkDetector.
// created before DI so the pre-DI config resolution can already use whatever state the shield reports.
internal sealed class MacNetworkState
{
    public string? Ssid { get; set; }
    public bool Wired { get; set; }
    public int? WifiAuthStatus { get; set; } // CLAuthorizationStatus raw value: 0=notDetermined 1=restricted 2=denied 3=authorized 4=authorizedAlways
}
