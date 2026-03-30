using System.Text.Json;
using Cathedral.Config;
using Microsoft.Extensions.Logging;

namespace Hydra.Config;

public class HydraConfig
{
    public List<ScreenDef> Screens { get; set; } = [];
    public LogLevel LogLevel { get; set; } = LogLevel.Information;

    public static HydraConfig Load(string path = "hydra.conf")
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<HydraConfig>(json, SaneJson.Options)
            ?? throw new InvalidOperationException("Failed to deserialize hydra.conf");
    }
}

public record ScreenDef(string Name, int X, int Y, int Width, int Height, bool IsVirtual);
