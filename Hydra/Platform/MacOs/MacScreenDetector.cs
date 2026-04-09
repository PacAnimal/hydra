using Hydra.Config;
using Hydra.Screen;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.MacOs;

public class MacScreenDetector(IHydraProfile profile, ILogger<MacScreenDetector> log)
    : ScreenDetector(profile, log)
{
    protected override List<DetectedScreen> Detect() => MacDisplayHelper.GetAllScreens();
}
