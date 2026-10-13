using System.Text.Json;
using Cathedral.Config;
using Hydra.Mouse;
using Hydra.Relay;
using Tests.Setup;

namespace Tests.Relay;

// A button injected down here is released wherever held keys are, since the master's ups cannot reach a slave it has
// lost, and a dormant one refuses them.
[TestFixture]
public class SlaveHeldButtonTests
{
    private const string Master = "master-pc";
    private static readonly MouseButtonMessage LeftDown = new(MouseButton.Left, true), LeftUp = new(MouseButton.Left, false);

    private static async Task<TestableSlaveRelay> Pressed()
    {
        var relay = new TestableSlaveRelay();
        await relay.SimulateConnected();
        await relay.SimulateMasterConfig(Master);
        await Send(relay, LeftDown);
        return relay;
    }

    private static Task Send(TestableSlaveRelay relay, MouseButtonMessage button) =>
        relay.SimulateReceive(Master, MessageKind.MouseButton, JsonSerializer.Serialize(button, SaneJson.Options));

    [Test]
    public async Task LeaveScreen_ReleasesAButtonHeldHere()
    {
        var relay = await Pressed();

        await relay.SimulateReceive(Master, MessageKind.LeaveScreen, "{}");

        Assert.That(relay.Output.Buttons, Is.EqualTo([LeftDown, LeftUp]));
    }

    [Test]
    public async Task Disconnect_ReleasesAButtonHeldHere()
    {
        var relay = await Pressed();

        await relay.SimulateDisconnected();

        Assert.That(relay.Output.Buttons, Is.EqualTo([LeftDown, LeftUp]));
    }

    [Test]
    public async Task GoingDormant_ReleasesAButtonHeldHere()
    {
        var relay = await Pressed();

        await relay.Dormancy.Enter();

        Assert.That(relay.Output.Buttons, Is.EqualTo([LeftDown, LeftUp]));
    }

    [Test]
    public async Task AButtonTheMasterReleased_IsNotReleasedAgain()
    {
        // the master's LeaveHost sends its ups ahead of LeaveScreen, on the same lane
        var relay = await Pressed();
        await Send(relay, LeftUp);

        await relay.SimulateReceive(Master, MessageKind.LeaveScreen, "{}");

        Assert.That(relay.Output.Buttons, Is.EqualTo([LeftDown, LeftUp]));
    }
}
