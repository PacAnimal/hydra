using Hydra.Keyboard;
using Hydra.Screen;
using Hydra.Relay;
using Tests.Setup;

namespace Tests.Relay;

// Dormant: the profile's conditions stopped matching for a reason a wake can undo (displays asleep,
// power reading flipped on the dock). We stay on the relay so a master can reach us, but refuse every
// input — and the arrival of that input is what wakes the machine back up.
[TestFixture]
public class SlaveDormancyTests
{
    private const string Master = "master-pc";

    private static async Task<(TestableSlaveRelay relay, FakeScreenSaverSync sync)> Setup(bool dormant = true)
    {
        var sync = new FakeScreenSaverSync();
        var relay = new TestableSlaveRelay(screenSaverSync: sync);
        await relay.SimulateConnected();
        await relay.SimulateMasterConfig(Master);
        if (dormant) await relay.Dormancy.Enter();
        relay.Sent.Clear();
        return (relay, sync);
    }

    // what a Mac reports once the externals have gone to sleep: one nameless, wrongly-sized display
    private static readonly LocalScreenSnapshot OneScreen = new(
        [new ScreenRect("phantom", "home", 0, 0, 1440, 900, IsLocal: true)],
        [new ScreenInfoEntry("phantom", 0, 0, 1440, 900, 1.0m)]);

    private static Task SendMove(TestableSlaveRelay relay) =>
        relay.SimulateReceive(Master, MessageKind.MouseMove, """{"screen":"home:0","x":10,"y":10}""");

    [Test]
    public async Task Dormant_MouseMove_IsDroppedAndWakesTheDisplay()
    {
        var (relay, sync) = await Setup();
        await SendMove(relay);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(relay.Output.TotalInjections, Is.Zero);
            Assert.That(sync.WakeDisplayCount, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Dormant_KeyEvent_IsDroppedAndWakesTheDisplay()
    {
        var (relay, sync) = await Setup();
        await relay.SimulateReceive(Master, MessageKind.KeyEvent, """{"type":0,"modifiers":0,"character":"a"}""");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(relay.Output.Keys, Is.Empty);
            Assert.That(sync.WakeDisplayCount, Is.EqualTo(1));
        }
    }

    // the first refused input starts the clock: match the profile again within it, or leave the relay
    [Test]
    public async Task Dormant_Input_ArmsTheWakeDeadline()
    {
        var (relay, _) = await Setup();
        await SendMove(relay);
        Assert.That(relay.Dormancy.RequestWake(), Is.False, "deadline should already be armed by the input");
    }

    [Test]
    public async Task Dormant_ActivityPing_ArmsTheWakeDeadline()
    {
        var (relay, _) = await Setup();
        await relay.SimulateReceive(Master, MessageKind.ActivityPing, "{}");
        Assert.That(relay.Dormancy.RequestWake(), Is.False, "deadline should already be armed by the ping");
    }

    [Test]
    public async Task Dormant_FloodOfInput_WakesOnlyOnce()
    {
        var (relay, sync) = await Setup();
        for (var i = 0; i < 20; i++) await SendMove(relay);
        Assert.That(sync.WakeDisplayCount, Is.EqualTo(1));
    }

    // a master only sends ActivityPing off the back of real local input, and we are dormant precisely
    // because the user walked away — so a ping means they are back at their desk. It usually beats any
    // input aimed at us, since they are working on the master before reaching over here.
    [Test]
    public async Task Dormant_ActivityPing_WakesTheDisplay()
    {
        var (relay, sync) = await Setup();
        await relay.SimulateReceive(Master, MessageKind.ActivityPing, "{}");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sync.WakeDisplayCount, Is.EqualTo(1));
            Assert.That(sync.ResetIdleTimerCalled, Is.False, "wake the panel properly, don't just poke the idle timer");
        }
    }

    [Test]
    public async Task Dormant_ClipboardHash_IsIgnored()
    {
        var (relay, _) = await Setup();
        await relay.SimulateReceive(Master, MessageKind.ClipboardHash, """{"hash":12345}""");
        Assert.That(relay.Sent, Is.Empty);
    }

    // a sleeping display enumerates to whatever the OS still lists — that is not our real geometry
    [Test]
    public async Task Dormant_IgnoresScreenChanges()
    {
        var (relay, _) = await Setup();
        relay.Screens.Snapshot = OneScreen;
        await relay.Screens.FireChange();
        Assert.That(relay.Sent, Is.Empty);
    }

    // the master must keep seeing us as a normal, fully-sized peer: it reconnects while we sleep, asks
    // everyone for geometry, and a remote-only master parks its cursor on whoever answers with real
    // dimensions. Answer with the phantom geometry, or not at all, and the cursor goes somewhere else.
    [Test]
    public async Task Dormant_AnnouncesLastAwakeGeometry_NotThePhantomOne()
    {
        var (relay, _) = await Setup();
        relay.Screens.Snapshot = OneScreen;

        await relay.SimulateMasterConfig("second-master");

        var (Targets, Kind, Json) = relay.Sent.Single(m => m.Kind == MessageKind.ScreenInfo);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Json, Does.Contain("home:0"), "must advertise the geometry we had while awake");
            Assert.That(Json, Does.Not.Contain("phantom"));
        }
    }

    [Test]
    public async Task Waking_ReAnnouncesScreens()
    {
        var (relay, _) = await Setup();
        await relay.Dormancy.Exit();
        Assert.That(relay.Sent.Select(m => m.Kind), Does.Contain(MessageKind.ScreenInfo));
    }

    // the master keeps its cursor parked on us while we sleep, so the KeyUp for anything held right now
    // never arrives — release it on the way in rather than waking with a stuck modifier
    [Test]
    public async Task GoingDormant_ReleasesHeldKeys()
    {
        var (relay, _) = await Setup(dormant: false);
        await relay.SimulateReceive(Master, MessageKind.KeyEvent, """{"type":0,"modifiers":1,"key":22}""");
        relay.Output.Keys.Clear();

        await relay.Dormancy.Enter();
        Assert.That(relay.Output.Keys.Select(k => k.Type), Is.EqualTo([KeyEventType.KeyUp]));
    }

    [Test]
    public async Task AfterWaking_InputIsInjectedAgain()
    {
        var (relay, sync) = await Setup();
        await relay.Dormancy.Exit();
        await SendMove(relay);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(relay.Output.MoveCount, Is.EqualTo(1));
            Assert.That(sync.WakeDisplayCount, Is.Zero);
        }
    }

    // the master parks its cursor on us and keeps it there while we sleep, so on waking we must already
    // know it is on screen — otherwise the local cursor stays hidden under a moving remote pointer
    [Test]
    public async Task Dormant_EnterScreen_RecordsTheMasterWithoutMovingTheCursor()
    {
        var cursor = new FakeCursorVisibility();
        var sync = new FakeScreenSaverSync();
        var relay = new TestableSlaveRelay(cursorHider: cursor, screenSaverSync: sync);
        await relay.SimulateConnected();
        await relay.SimulateMasterConfig(Master);
        await relay.Dormancy.Enter();

        await relay.SimulateReceive(Master, MessageKind.EnterScreen, """{"screen":"home:0","x":5,"y":5,"width":100,"height":100}""");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(relay.Output.MoveCount, Is.Zero, "cursor must not be moved while dormant");
            Assert.That(sync.WakeDisplayCount, Is.EqualTo(1));
        }

        await relay.Dormancy.Exit();
        await SendMove(relay);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(relay.Output.MoveCount, Is.EqualTo(1));
            Assert.That(cursor.IsHidden, Is.False, "master is on screen — local cursor must be visible");
        }
    }

    [Test]
    public async Task NotDormant_MouseMove_IsInjectedWithoutWaking()
    {
        var (relay, sync) = await Setup(dormant: false);
        await SendMove(relay);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(relay.Output.MoveCount, Is.EqualTo(1));
            Assert.That(sync.WakeDisplayCount, Is.Zero);
        }
    }
}
