using System.Text.Json;
using Cathedral.Config;
using Hydra.Screen;
using Microsoft.Extensions.Logging;

namespace Hydra.Config;

public class HydraConfig
{
    public List<ScreenRect> Screens { get; set; } = [];
    public LogLevel LogLevel { get; set; } = LogLevel.Information;

    public static HydraConfig Load(string path = "hydra.conf")
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<HydraConfig>(json, SaneJson.Options)
            ?? throw new InvalidOperationException("Failed to deserialize hydra.conf");
    }
}
