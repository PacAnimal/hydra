using static System.FormattableString;
using Hydra.Platform;
using Hydra.Screen;
using Tests.Setup;

namespace Tests.Platform;

// the figures every platform's OSD draws to; the macOS shield's Swift has its own copy of them, checked here
[TestFixture]
public class OsdStyleTests
{
    [Test]
    public void TheSharedFigures_MatchTheMacShield()
    {
        var swift = File.ReadAllText(Path.Combine(TestLog.SolutionRoot, "Hydra", "Resources", "MacShield", "main.swift"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(swift, Does.Contain(Invariant($"systemFont(ofSize: {OsdStyle.EmSize}, weight: .bold)")));
            Assert.That(swift, Does.Contain(Invariant($"dismissTimer = Timer.scheduledTimer(withTimeInterval: {OsdStyle.ShowFor.TotalSeconds},")));
            Assert.That(swift, Does.Contain(Invariant($"ctx.duration = {OsdStyle.FadeFor.TotalSeconds}")));
            Assert.That(swift, Does.Contain("screen.frame.height * 0.1"));
        }
    }

    [Test]
    public void TheOsd_IsCentred_ATenthOfTheScreensHeightAboveItsBottom()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(OsdStyle.Place(new ScreenBounds(0, 0, 2560, 1440), 300, 60), Is.EqualTo(new ScreenPoint(1130, 1440 - 60 - 144)));
            Assert.That(OsdStyle.Place(new ScreenBounds(2560, 0, 1920, 1080), 300, 60), Is.EqualTo(new ScreenPoint(2560 + 810, 1080 - 60 - 108)));
        }
    }
}
