using Hydra.Screen;
using Microsoft.Extensions.Logging;
using Tests.Setup;

namespace Tests.Screen;

// A key held here keeps the cursor home: a visit starting now would take the typing mid-chord.
[TestFixture]
public class HeldKeyEntryTests : ServicesFixture
{
    private Task<TestServiceBundle> Started(ILogger<InputRouter>? log = null) =>
        Services.StartOnline(TransitionTestHelper.CreateService(log: log), "remote");

    private static int Refusals(CapturingLog<InputRouter> log) => log.Messages.Count(m => m.Contains("a key is held"));

    [Test]
    public async Task Edge_WhileAKeyIsHeld_StaysHomeUntilItsUp()
    {
        var bundle = await Started();
        bundle.Platform.AnyKeyHeld = true;

        bundle.Platform.FireMouseMove(2559, 720);
        var whileHeld = bundle.Platform.IsOnVirtualScreen;
        bundle.Platform.AnyKeyHeld = false;
        bundle.Platform.FireMouseMove(2559, 721);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(whileHeld, Is.False, "refused while the key is held");
            Assert.That(bundle.Platform.IsOnVirtualScreen, Is.True, "entered on the next move after its up");
        }
    }

    [Test]
    public async Task RefusedEntry_IsLoggedOncePerHold()
    {
        var log = new CapturingLog<InputRouter>(LogLevel.Information);
        var bundle = await Started(log);
        bundle.Platform.AnyKeyHeld = true;

        for (var y = 700; y < 710; y++) bundle.Platform.FireMouseMove(2559, y);
        var once = Refusals(log);
        bundle.Platform.AnyKeyHeld = false;
        TransitionTestHelper.EnterRemote(bundle.Platform);
        bundle.Platform.PressHotkey('l');
        bundle.Platform.PressHotkey('l');
        bundle.Platform.FireMouseMove(1000, 720);
        bundle.Platform.AnyKeyHeld = true;
        bundle.Platform.FireMouseMove(2559, 720);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(once, Is.EqualTo(1), "one line however often the edge is hit");
            Assert.That(Refusals(log), Is.EqualTo(2), "another for the next hold");
        }
    }

    [Test]
    public async Task RemoteOnlyAutoEntry_WhileAKeyIsHeld_IsRetriedOnTheNextLocalMove()
    {
        var created = TransitionTestHelper.CreateRemoteOnlyService();
        created.Platform.AnyKeyHeld = true;
        var bundle = await Services.StartOnline(created, "mac");
        var whileHeld = bundle.Platform.IsOnVirtualScreen;

        bundle.Platform.AnyKeyHeld = false;
        bundle.Platform.FireMouseMove(100, 100);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(whileHeld, Is.False, "refused while the key is held");
            Assert.That(bundle.Platform.IsOnVirtualScreen, Is.True, "entered on the next local move");
        }
    }
}
