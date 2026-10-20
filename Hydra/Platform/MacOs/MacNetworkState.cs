namespace Hydra.Platform.MacOs;

internal sealed class MacNetworkState
{
    public string? Ssid { get; set; }
    public int? WifiAuthStatus { get; set; } // CLAuthorizationStatus raw value: 0=notDetermined 1=restricted 2=denied 3=authorized 4=authorizedAlways

    // a CLAuthorizationStatus for the log; none before the shield has reported one
    internal static string AuthLabel(int? status) => status switch
    {
        null => "none",
        0 => "notDetermined",
        1 => "restricted",
        2 => "denied",
        3 or 4 => "authorized",
        _ => status.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}
