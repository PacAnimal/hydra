using Hydra.Platform;
using Hydra.Screen;

namespace Tests.Platform;

// the window last worked in on a screen: the frontmost one, as the platform stacks them, whose centre lies on it
[TestFixture]
public class WindowPickerTests
{
    private static readonly ScreenBounds Left = new(0, 0, 1920, 1080);
    private static readonly ScreenBounds Right = new(1920, 0, 1920, 1080);

    [Test]
    public void TheFrontmostOnTheScreen_Wins()
    {
        var windows = new[] { Window(1, 2000, 100), Window(2, 100, 100), Window(3, 200, 200) };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(WindowPicker.FrontmostOn(windows, Left)?.Id, Is.EqualTo((nint)2));
            Assert.That(WindowPicker.FrontmostOn(windows, Right)?.Id, Is.EqualTo((nint)1));
        }
    }

    // a window straddling two screens belongs to the one holding its centre
    [Test]
    public void AWindowAcrossTwoScreens_IsOnTheOneHoldingItsCentre()
    {
        var straddling = new ListedWindow(1, 0, new ScreenBounds(1500, 100, 1000, 600));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(WindowPicker.FrontmostOn([straddling], Right)?.Id, Is.EqualTo((nint)1));
            Assert.That(WindowPicker.FrontmostOn([straddling], Left), Is.Null);
        }
    }

    [Test]
    public void NoWindowOnTheScreen_PicksNone() => Assert.That(WindowPicker.FrontmostOn([Window(1, 2000, 100)], Left), Is.Null);

    [Test]
    public void AnEmptyWindow_IsPassedOver()
    {
        var windows = new[] { new ListedWindow(1, 0, new ScreenBounds(100, 100, 0, 0)), Window(2, 300, 300) };

        Assert.That(WindowPicker.FrontmostOn(windows, Left)?.Id, Is.EqualTo((nint)2));
    }

    // a 400x300 window at (x, y)
    private static ListedWindow Window(nint id, int x, int y) => new(id, 0, new ScreenBounds(x, y, 400, 300));
}
