namespace Hydra.Config;

public class ConfigConditions
{
    public string? Ssid { get; init; }
    public int? ScreenCount { get; init; }

    internal bool IsEmpty => Ssid == null && ScreenCount == null;
}
