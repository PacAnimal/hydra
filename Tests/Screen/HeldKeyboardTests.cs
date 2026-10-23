using Hydra.Screen;
using Microsoft.Extensions.Logging;
using Tests.Setup;

namespace Tests.Screen;

// Another client holding the keyboard (Xorg: an open dmenu, a window manager's menu) keeps the cursor home: a visit would
// take the mouse to the remote and leave the typing here.
[TestFixture]
public class HeldKeyboardTests : ServicesFixture
{
    private Task<TestServiceBundle> Started(ILogger<InputRouter>? log = null) =>
        Services.StartOnline(TransitionTestHelper.CreateService(log: log), "remote");

    private static int Refusals(CapturingLog<InputRouter> log) => log.Messages.Count(m => m.Contains("holds the keyboard"));

    [Test]
    public async Task Edge_WhileAnotherClientHoldsTheKeyboard_StaysHomeUntilItLetsGo()
    {
        var bundle = await Started();
        bundle.Platform.KeyboardHeldElsewhere = true;

        bundle.Platform.FireMouseMove(2559, 720);
        var whileHeld = bundle.Platform.IsOnVirtualScreen;
        bundle.Platform.KeyboardHeldElsewhere = false;
        bundle.Platform.FireMouseMove(2559, 721);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(whileHeld, Is.False, "refused while the keyboard is held");
            Assert.That(bundle.Platform.IsOnVirtualScreen, Is.True, "entered once it is let go");
        }
    }

    [Test]
    public async Task RefusedEntry_IsLoggedOncePerHold()
    {
        var log = new CapturingLog<InputRouter>(LogLevel.Information);
        var bundle = await Started(log);
        bundle.Platform.KeyboardHeldElsewhere = true;

        for (var y = 700; y < 710; y++) bundle.Platform.FireMouseMove(2559, y);
        var once = Refusals(log);
        bundle.Platform.KeyboardHeldElsewhere = false;
        TransitionTestHelper.EnterRemote(bundle.Platform);
        bundle.Platform.PressHotkey('l');
        bundle.Platform.PressHotkey('l');
        bundle.Platform.FireMouseMove(1000, 720);
        bundle.Platform.KeyboardHeldElsewhere = true;
        bundle.Platform.FireMouseMove(2559, 720);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(once, Is.EqualTo(1), "one line however often the edge is hit");
            Assert.That(Refusals(log), Is.EqualTo(2), "another for the next hold");
        }
    }

    [Test]
    public async Task RemoteOnlyAutoEntry_WhileAnotherClientHoldsTheKeyboard_IsRetriedOnTheNextLocalMove()
    {
        var created = TransitionTestHelper.CreateRemoteOnlyService();
        created.Platform.KeyboardHeldElsewhere = true;
        var bundle = await Services.StartOnline(created, "mac");
        var whileHeld = bundle.Platform.IsOnVirtualScreen;

        bundle.Platform.KeyboardHeldElsewhere = false;
        bundle.Platform.FireMouseMove(100, 100);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(whileHeld, Is.False, "refused while the keyboard is held");
            Assert.That(bundle.Platform.IsOnVirtualScreen, Is.True, "entered on the next local move");
        }
    }
}
