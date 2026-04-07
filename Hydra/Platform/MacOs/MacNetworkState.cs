namespace Hydra.Platform.MacOs;

internal sealed class MacNetworkState
{
    public string? Ssid { get; set; }
    public int? WifiAuthStatus { get; set; } // CLAuthorizationStatus raw value: 0=notDetermined 1=restricted 2=denied 3=authorized 4=authorizedAlways
}
