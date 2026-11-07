using Cathedral.Extensions;
using Hydra.Relay;
using Tests.Setup;

namespace Tests.Relay;

// a slave's invertScrollX/invertScrollY flip what it is sent before injecting, whatever its platform
[TestFixture]
public class SlaveScrollInversionTests
{
    private const string Master = "master-pc";

    private readonly SlaveRelays _slaves = new();

    [TearDown]
    public Task StopSlaves() => _slaves.StopAll();

    [TestCase(false, false, 120, -60)]
    [TestCase(true, false, -120, -60)]
    [TestCase(false, true, 120, 60)]
    [TestCase(true, true, -120, 60)]
    public async Task Scroll_IsInjectedFlippedOnTheAxesAsked(bool invertX, bool invertY, int expectedX, int expectedY)
    {
        var slave = await Slave(invertX, invertY);

        await slave.SimulateReceive(Master, MessageKind.MouseScroll, new MouseScrollMessage(120, -60).ToSaneJson());

        Assert.That(slave.Output.Scrolls, Is.EqualTo([new MouseScrollMessage((short)expectedX, (short)expectedY)]));
    }

    private async Task<TestableSlaveRelay> Slave(bool invertX, bool invertY)
    {
        var slave = _slaves.Add(new TestableSlaveRelay(invertScrollX: invertX, invertScrollY: invertY));
        await slave.SimulateConnected();
        await slave.SimulateMasterConfig(Master);
        return slave;
    }
}
