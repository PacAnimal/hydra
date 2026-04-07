using Hydra.Config;
using Hydra.Screen;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Windows;

public class WindowsScreenDetector(HydraConfig config, ILogger<WindowsScreenDetector> log)
    : ScreenDetector(config, log)
{
    protected override List<DetectedScreen> Detect() => WindowsDisplayHelper.GetAllScreens();
}
