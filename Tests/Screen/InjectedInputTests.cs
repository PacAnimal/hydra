using Hydra.Relay;
using Hydra.Screen;
using Microsoft.Extensions.Logging;
using Tests.Setup;

namespace Tests.Screen;

// Input another program injects into the master's own display during a visit (Xorg: a VNC server's XTest) is someone at
// the master, so the visit ends and the cursor lands where that input pointed.
[TestFixture]
public class InjectedInputTests : ServicesFixture
{
    private Task<TestServiceBundle> Visiting(ILogger<InputRouter>? log = null) =>
        Services.Visiting(TransitionTestHelper.CreateService(log: log));

    [Test]
    public async Task InjectedInput_DuringAVisit_ReturnsHomeWhereItPointsAndTellsTheHost()
    {
        var bundle = await Visiting();

        bundle.Platform.FireInjectedInput(new ScreenPoint(400, 300));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bundle.Platform.IsOnVirtualScreen, Is.False, "home");
            Assert.That((bundle.Platform.WarpX, bundle.Platform.WarpY), Is.EqualTo((400, 300)), "where the injected input pointed");
            Assert.That(bundle.Relay.Sent.Of(MessageKind.LeaveScreen, "remote"), Is.Not.Empty, "the host is told to leave");
        }
    }

    [Test]
    public async Task InjectedInput_OnNoLocalScreen_LandsAsAReturnWithNoEdgeOfItsOwn()
    {
        var bundle = await Visiting();

        bundle.Platform.FireInjectedInput(new ScreenPoint(50_000, 50_000));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bundle.Platform.IsOnVirtualScreen, Is.False);
            Assert.That((bundle.Platform.WarpX, bundle.Platform.WarpY), Is.Not.EqualTo((50_000, 50_000)), "never off every screen");
        }
    }

    [Test]
    public async Task InjectedInput_WithNoPointer_StillReturnsHome()
    {
        var bundle = await Visiting();

        bundle.Platform.FireInjectedInput(null);

        Assert.That(bundle.Platform.IsOnVirtualScreen, Is.False);
    }

    [Test]
    public async Task InjectedInput_IsLoggedWithTheHostLeft()
    {
        var log = new CapturingLog<InputRouter>(LogLevel.Information);
        var bundle = await Visiting(log);

        bundle.Platform.FireInjectedInput(new ScreenPoint(400, 300));

        Assert.That(log.Messages.Where(m => m.Contains("input was injected on this display")), Has.One.Contains("'remote'"));
    }

    [Test]
    public async Task InjectedInput_AtHome_DoesNothing()
    {
        var log = new CapturingLog<InputRouter>(LogLevel.Information);
        var bundle = await Services.StartOnline(TransitionTestHelper.CreateService(log: log), "remote");
        bundle.Relay.Sent.Clear();
        var warps = bundle.Platform.WarpCount;

        bundle.Platform.FireInjectedInput(new ScreenPoint(400, 300));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bundle.Platform.IsOnVirtualScreen, Is.False);
            Assert.That(bundle.Platform.WarpCount, Is.EqualTo(warps), "the pointer is left where it is");
            Assert.That(bundle.Relay.Sent.Of(MessageKind.LeaveScreen), Is.Empty, "nothing to leave");
            Assert.That(log.Messages, Has.None.Contains("input was injected"));
        }
    }

    [Test]
    public async Task InjectedInput_BeforeTheVisitWasAnnounced_TellsTheHostNothing()
    {
        // a visit still waiting for its keyboard grab was never announced, so the host has nothing to leave
        var bundle = await Services.StartOnline(TransitionTestHelper.CreateService(), "remote");
        bundle.Platform.KeyboardCaptured = false;
        bundle.Platform.FireMouseMove(2559, 720);
        Assert.That(bundle.Platform.IsOnVirtualScreen, Is.True, "pre-condition: the cursor left home");

        bundle.Platform.FireInjectedInput(new ScreenPoint(400, 300));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bundle.Platform.IsOnVirtualScreen, Is.False, "home");
            Assert.That(bundle.Relay.Sent.TargetsOf(MessageKind.EnterScreen), Has.None.EqualTo("remote"), "never entered");
            Assert.That(bundle.Relay.Sent.TargetsOf(MessageKind.LeaveScreen), Has.None.EqualTo("remote"), "so never left");
        }
    }

    [Test]
    public async Task AfterInjectedInput_TheEdgeEntersAgain()
    {
        var bundle = await Visiting();
        bundle.Platform.FireInjectedInput(new ScreenPoint(1000, 720));

        bundle.Platform.FireMouseMove(1000, 720);
        TransitionTestHelper.EnterRemote(bundle.Platform);

        Assert.That(bundle.Platform.IsOnVirtualScreen, Is.True);
    }
}
