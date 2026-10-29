using Hydra.Platform;
using Hydra.Relay;
using Microsoft.Extensions.Logging;

namespace Tests.Setup;

// the TestableSlaveRelays a fixture made, stopped from its [TearDown]; never started, so stopping only disposes the
// stream sender and receiver
public sealed class SlaveRelays
{
    private readonly List<TestableSlaveRelay> _slaves = [];

    public TestableSlaveRelay Add(TestableSlaveRelay slave)
    {
        _slaves.Add(slave);
        return slave;
    }

    // a slave on clipboard, its OSD and clock as given, stopped with the rest
    public TestableSlaveRelay New(IClipboardSync clipboard, IOsdNotification osd, TimeProvider clock, ILogger<RelayConnection>? log = null) =>
        Add(new TestableSlaveRelay(clipboard: clipboard, osd: osd, clock: clock, log: log));

    public async Task StopAll()
    {
        foreach (var slave in _slaves)
        {
            slave.Reliable.Release();
            await slave.StopAsync(CancellationToken.None).WaitAsync(Bound);
            slave.Dispose();
        }
        _slaves.Clear();
    }
}
