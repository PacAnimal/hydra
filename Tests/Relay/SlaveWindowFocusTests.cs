using Cathedral.Extensions;
using Hydra.Relay;
using Hydra.Screen;
using Tests.Setup;

namespace Tests.Relay;

// a slave the monitor switch hotkey arrives on brings forward the window last worked in on that screen of its own
[TestFixture]
public class SlaveWindowFocusTests
{
    private const string Master = "master-pc";

    private readonly SlaveRelays _slaves = new();

    [TearDown]
    public Task StopSlaves() => _slaves.StopAll();

    [Test]
    public async Task AnEntryAskingForFocus_FocusesOnThatScreen()
    {
        var (slave, focus) = await Slave();

        await Enter(slave, focus: true);

        Assert.That(focus.Focused, Is.EqualTo([new ScreenBounds(0, 0, 2560, 1440)]));
    }

    [Test]
    public async Task AnOrdinaryEntry_FocusesNothing()
    {
        var (slave, focus) = await Slave();

        await Enter(slave, focus: false);

        Assert.That(focus.Focused, Is.Empty);
    }

    // dormant, the cursor is not moved either; the user is not here to want the window
    [Test]
    public async Task WhileDormant_NothingIsFocused()
    {
        var (slave, focus) = await Slave();
        await slave.Dormancy.Enter();

        await Enter(slave, focus: true);

        Assert.That(focus.Focused, Is.Empty);
    }

    [Test]
    public async Task AScreenItDoesNotHave_FocusesNothing()
    {
        var (slave, focus) = await Slave();

        await slave.SimulateReceive(Master, MessageKind.EnterScreen, new EnterScreenMessage("elsewhere", 5, 5, 100, 100, Focus: true).ToSaneJson());

        Assert.That(focus.Focused, Is.Empty);
    }

    private async Task<(TestableSlaveRelay Slave, RecordingWindowFocus Focus)> Slave()
    {
        var focus = new RecordingWindowFocus();
        var slave = _slaves.Add(new TestableSlaveRelay(windowFocus: focus));
        await slave.SimulateConnected();
        await slave.SimulateMasterConfig(Master);
        return (slave, focus);
    }

    private static Task Enter(TestableSlaveRelay slave, bool focus) =>
        slave.SimulateReceive(Master, MessageKind.EnterScreen, new EnterScreenMessage("home:0", 1280, 720, 2560, 1440, focus).ToSaneJson());
}
