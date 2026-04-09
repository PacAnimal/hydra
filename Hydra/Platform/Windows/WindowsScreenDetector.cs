using Hydra.Config;
using Hydra.Screen;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Windows;

public class WindowsScreenDetector(IHydraProfile profile, ILogger<WindowsScreenDetector> log)
    : ScreenDetector(profile, log)
{
    protected override List<DetectedScreen> Detect() => WindowsDisplayHelper.GetAllScreens();
}
