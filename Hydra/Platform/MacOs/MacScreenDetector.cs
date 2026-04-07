using Hydra.Config;
using Hydra.Screen;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.MacOs;

public class MacScreenDetector(HydraConfig config, ILogger<MacScreenDetector> log)
    : ScreenDetector(config, log)
{
    protected override List<DetectedScreen> Detect() => MacDisplayHelper.GetAllScreens();
}
