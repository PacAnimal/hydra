using Cathedral.Extensions;
using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Relay;
using Tests.Setup;

namespace Tests.Relay;

// a monitor switch that started where the cursor was hidden arrives with it hidden, until the mouse is used; one that
// started with it shown shows it
[TestFixture]
public class SlaveSwitchCursorTests
{
    private const string Master = "master-pc";

    private readonly SlaveRelays _slaves = new();

    [TearDown]
    public Task StopSlaves() => _slaves.StopAll();

    [Test]
    public async Task AnOrdinaryEntry_ShowsTheCursor()
    {
        var (slave, cursor) = await Slave();

        await slave.SimulateReceive(Master, MessageKind.EnterScreen, new EnterScreenMessage("home:0", 5, 5, 2560, 1440).ToSaneJson());

        Assert.That(cursor.IsHidden, Is.False);
    }

    [Test]
    public async Task ASwitchWithTheCursorShown_ShowsIt()
    {
        var (slave, cursor) = await Slave();

        await Enter(slave, hidden: false);

        Assert.That(cursor.IsHidden, Is.False);
    }

    [Test]
    public async Task ASwitchWithTheCursorHidden_KeepsItHidden()
    {
        var (slave, cursor) = await Slave();

        await Enter(slave, hidden: true);

        Assert.That(cursor.IsHidden, Is.True);
    }

    [Test]
    public async Task AfterTheSwitch_TypingKeepsTheCursorHidden()
    {
        var (slave, cursor) = await Slave();
        await Enter(slave, hidden: true);

        await slave.SimulateReceive(Master, MessageKind.KeyEvent, new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.None, 'a', null).ToSaneJson());

        Assert.That(cursor.IsHidden, Is.True);
    }

    [Test]
    public async Task AfterTheSwitch_MovingTheMouseShowsTheCursor()
    {
        var (slave, cursor) = await Slave();
        await Enter(slave, hidden: true);

        await slave.SimulateReceive(Master, MessageKind.MouseMove, new MouseMoveMessage("home:0", 1290, 720).ToSaneJson());

        Assert.That(cursor.IsHidden, Is.False);
    }

    [Test]
    public async Task AfterTheSwitch_AClickShowsTheCursor()
    {
        var (slave, cursor) = await Slave();
        await Enter(slave, hidden: true);

        await slave.SimulateReceive(Master, MessageKind.MouseButton, new MouseButtonMessage(MouseButton.Left, true).ToSaneJson());

        Assert.That(cursor.IsHidden, Is.False);
    }

    // once the mouse has shown it, keys go back to showing it as they always have
    [Test]
    public async Task OnceTheMouseHasMoved_TypingShowsTheCursorAgain()
    {
        var (slave, cursor) = await Slave();
        await Enter(slave, hidden: true);
        await slave.SimulateReceive(Master, MessageKind.MouseMove, new MouseMoveMessage("home:0", 1290, 720).ToSaneJson());
        cursor.Hide();

        await slave.SimulateReceive(Master, MessageKind.KeyEvent, new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.None, 'a', null).ToSaneJson());

        Assert.That(cursor.IsHidden, Is.False);
    }

    // a later entry over an edge is the mouse's, and shows it at once
    [Test]
    public async Task AnOrdinaryEntryAfterTheSwitch_ShowsTheCursor()
    {
        var (slave, cursor) = await Slave();
        await Enter(slave, hidden: true);
        await slave.SimulateReceive(Master, MessageKind.LeaveScreen, new LeaveScreenMessage().ToSaneJson());

        await slave.SimulateReceive(Master, MessageKind.EnterScreen, new EnterScreenMessage("home:0", 5, 5, 2560, 1440).ToSaneJson());

        Assert.That(cursor.IsHidden, Is.False);
    }

    private async Task<(TestableSlaveRelay Slave, FakeCursorVisibility Cursor)> Slave()
    {
        var cursor = new FakeCursorVisibility();
        var slave = _slaves.Add(new TestableSlaveRelay(cursorHider: cursor));
        await slave.SimulateConnected();
        await slave.SimulateMasterConfig(Master);
        return (slave, cursor);
    }

    private static Task Enter(TestableSlaveRelay slave, bool hidden) =>
        slave.SimulateReceive(Master, MessageKind.EnterScreen, new EnterScreenMessage("home:0", 1280, 720, 2560, 1440, Focus: true, CursorHidden: hidden).ToSaneJson());
}
