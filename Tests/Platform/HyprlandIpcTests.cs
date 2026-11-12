using Hydra.Platform.Linux;

namespace Tests.Platform;

[TestFixture]
public class HyprlandIpcTests
{
    private static string Monitor(string name, int x, int y, int w, int h, double scale = 1, int transform = 0, bool disabled = false) =>
        $$"""{"name":"{{name}}","description":"{{name}} panel","x":{{x}},"y":{{y}},"width":{{w}},"height":{{h}},"scale":{{scale}},"transform":{{transform}},"disabled":{{(disabled ? "true" : "false")}}}""";

    [Test]
    public void SizesAreLogical_RotatedThenScaled()
    {
        var json = $"[{Monitor("DP-1", 0, 0, 3840, 2160, scale: 2)},{Monitor("DP-2", 1920, 0, 2560, 1440, transform: 1)},{Monitor("HDMI-A-1", 3360, 0, 1920, 1080, transform: 2)}]";

        var monitors = HyprlandIpc.ParseMonitors(json);

        Assert.That(monitors, Is.EqualTo(new[]
        {
            new HyprlandIpc.Monitor("DP-1", "DP-1 panel", 0, 0, 1920, 1080),
            new HyprlandIpc.Monitor("DP-2", "DP-2 panel", 1920, 0, 1440, 2560),
            new HyprlandIpc.Monitor("HDMI-A-1", "HDMI-A-1 panel", 3360, 0, 1920, 1080),
        }));
    }

    [Test]
    public void DisabledMonitorsAreLeftOut()
    {
        var json = $"[{Monitor("DP-1", 0, 0, 1920, 1080)},{Monitor("DP-2", 1920, 0, 1920, 1080, disabled: true)}]";

        Assert.That(HyprlandIpc.ParseMonitors(json).Select(m => m.Name), Is.EqualTo(new[] { "DP-1" }));
    }

    [Test]
    public void FractionalScaleRounds()
    {
        var json = $"[{Monitor("eDP-1", 0, 0, 2880, 1800, scale: 1.6)}]";

        var m = HyprlandIpc.ParseMonitors(json).Single();

        Assert.That((m.Width, m.Height), Is.EqualTo((1800, 1125)));
    }
}
