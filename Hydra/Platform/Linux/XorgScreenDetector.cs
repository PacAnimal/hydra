using Hydra.Config;
using Hydra.Screen;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Linux;

public class XorgScreenDetector : ScreenDetector
{
    private readonly nint _display;
    private readonly nint _rootWindow;

    public XorgScreenDetector(IHydraProfile profile, ILogger<XorgScreenDetector> log) : base(profile, log)
    {
        _ = NativeMethods.XInitThreads();
        _display = NativeMethods.XOpenDisplay(null);
        if (_display == nint.Zero)
            throw new InvalidOperationException("XOpenDisplay failed — is DISPLAY set?");
        _rootWindow = NativeMethods.XDefaultRootWindow(_display);
    }

    protected override List<DetectedScreen> Detect() => XorgDisplayHelper.GetAllScreens(_display, _rootWindow);
}
