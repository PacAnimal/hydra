using Hydra.Mouse;
using Hydra.Relay;
using Hydra.Screen;
using Microsoft.Extensions.Logging;
using Tests.Setup;

namespace Tests.Screen;

// A visit whose grab could not be had (Xorg: an open dmenu keeps the keyboard through every retry) would take the mouse
// and leave the typing here, so it ends and the cursor comes home.
[TestFixture]
public class CaptureFailedTests : ServicesFixture
{
    private Task<TestServiceBundle> Visiting(ILogger<InputRouter>? log = null) =>
        Services.Visiting(TransitionTestHelper.CreateService(log: log));

    [Test]
    public async Task FailedCapture_DuringAVisit_ReturnsHomeAndTellsTheHost()
    {
        var bundle = await Visiting();

        bundle.Platform.FireCaptureFailed();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bundle.Platform.IsOnVirtualScreen, Is.False, "home");
            Assert.That(bundle.Relay.Sent.Of(MessageKind.LeaveScreen, "remote"), Is.Not.Empty, "the host is told to leave");
        }
    }

    [Test]
    public async Task FailedCapture_IsLoggedWithTheHostLeft()
    {
        var log = new CapturingLog<InputRouter>(LogLevel.Information);
        var bundle = await Visiting(log);

        bundle.Platform.FireCaptureFailed();

        var warned = log.Entries.Where(e => e.Level == LogLevel.Warning && e.Message.Contains("holds the keyboard or pointer")).ToList();
        Assert.That(warned.Select(e => e.Message), Has.One.Contains("'remote'"));
    }

    [Test]
    public async Task FailedCapture_AfterTheVisitEnded_DoesNothing()
    {
        var log = new CapturingLog<InputRouter>(LogLevel.Information);
        var bundle = await Services.StartOnline(TransitionTestHelper.CreateService(log: log), "remote");
        bundle.Relay.Sent.Clear();

        bundle.Platform.FireCaptureFailed();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bundle.Platform.IsOnVirtualScreen, Is.False);
            Assert.That(bundle.Relay.Sent.Of(MessageKind.LeaveScreen), Is.Empty, "nothing to leave");
            Assert.That(log.Messages, Has.None.Contains("holds the keyboard or pointer"));
        }
    }

    [Test]
    public async Task AfterAFailedCapture_TheEdgeEntersAgain()
    {
        // the next crossing tries again; a grab let go in the meantime is had this time
        var bundle = await Visiting();
        bundle.Platform.FireCaptureFailed();

        bundle.Platform.FireMouseMove(1000, 720);
        TransitionTestHelper.EnterRemote(bundle.Platform);

        Assert.That(bundle.Platform.IsOnVirtualScreen, Is.True);
    }

    private static int Moves(FakeRelay relay) =>
        relay.Sent.Of(MessageKind.MouseMove).Count + relay.Sent.Of(MessageKind.MouseMoveDelta).Count;

    [TestCase(false)]
    [TestCase(true)]
    public async Task WhileTheKeyboardIsNotCaptured_NothingMovesOnTheRemote(bool delta)
    {
        var bundle = await Visiting();
        bundle.Platform.KeyboardCaptured = false;
        bundle.Relay.Sent.Clear();

        for (var i = 0; i < 20; i++)
        {
            if (delta) bundle.Platform.FireMouseDelta(-30, 5);
            else bundle.Platform.FireMouseMove(bundle.Platform.WarpX - 30, bundle.Platform.WarpY + 5);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Moves(bundle.Relay), Is.Zero, "no motion sent");
            Assert.That(bundle.Platform.IsOnVirtualScreen, Is.True, "still visiting, not pushed home through an edge");
        }
    }

    [Test]
    public async Task OnceTheKeyboardIsCaptured_MotionReachesTheRemote()
    {
        var bundle = await Visiting();
        bundle.Platform.KeyboardCaptured = false;
        bundle.Platform.FireMouseDelta(-30, 5);
        bundle.Platform.KeyboardCaptured = true;
        bundle.Relay.Sent.Clear();

        for (var i = 0; i < 20; i++) bundle.Platform.FireMouseDelta(-30, 5);

        Assert.That(Moves(bundle.Relay), Is.Positive);
    }

    // crosses into "remote" from home with the keyboard grab still missing
    private async Task<TestServiceBundle> EnteredWithoutTheKeyboard()
    {
        var bundle = await Services.StartOnline(TransitionTestHelper.CreateService(), "remote");
        bundle.Platform.KeyboardCaptured = false;
        bundle.Platform.FireMouseMove(2559, 720);
        Assert.That(bundle.Platform.IsOnVirtualScreen, Is.True, "pre-condition: the cursor left home");
        return bundle;
    }

    [Test]
    public async Task EntryWithoutTheKeyboard_IsNotAnnounced_UntilARetryWinsIt()
    {
        var bundle = await EnteredWithoutTheKeyboard();
        var beforeWin = bundle.Relay.Sent.Of<EnterScreenMessage>(MessageKind.EnterScreen, "remote").Count;

        bundle.Platform.KeyboardCaptured = true;
        bundle.Platform.FireKeyboardCapturedLate();

        var entered = bundle.Relay.Sent.Of<EnterScreenMessage>(MessageKind.EnterScreen, "remote");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(beforeWin, Is.Zero, "no cursor shows there while the keys still type here");
            Assert.That(entered, Has.Count.EqualTo(1), "announced once the keyboard is ours");
        }
    }

    [Test]
    public async Task EntryWithoutTheKeyboard_GivenUp_EndsWithoutTheHostEverHearingOfIt()
    {
        var bundle = await EnteredWithoutTheKeyboard();

        bundle.Platform.FireCaptureFailed();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bundle.Platform.IsOnVirtualScreen, Is.False, "home");
            Assert.That(bundle.Relay.Sent.TargetsOf(MessageKind.EnterScreen), Has.None.EqualTo("remote"), "never entered");
            Assert.That(bundle.Relay.Sent.TargetsOf(MessageKind.LeaveScreen), Has.None.EqualTo("remote"), "so never left");
        }
    }

    [Test]
    public async Task EntryWithoutTheKeyboard_ForwardsNoButtonsOrScroll()
    {
        var bundle = await EnteredWithoutTheKeyboard();

        bundle.Platform.FireMouseButton(new MouseButtonEvent(MouseButton.Left, true));
        bundle.Platform.FireMouseButton(new MouseButtonEvent(MouseButton.Left, false));
        bundle.Platform.FireMouseScroll(new MouseScrollEvent(0, 3));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bundle.Relay.Sent.Of(MessageKind.MouseButton), Is.Empty);
            Assert.That(bundle.Relay.Sent.Of(MessageKind.MouseScroll), Is.Empty);
        }
    }

    [Test]
    public async Task LateCapture_AfterTheVisitEnded_AnnouncesNothing()
    {
        var bundle = await EnteredWithoutTheKeyboard();
        bundle.Platform.FireCaptureFailed();

        bundle.Platform.FireKeyboardCapturedLate();

        Assert.That(bundle.Relay.Sent.TargetsOf(MessageKind.EnterScreen), Has.None.EqualTo("remote"));
    }
}
