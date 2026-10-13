using Hydra.Config;
using Hydra.FileTransfer;
using Hydra.Platform;
using Hydra.Relay;
using Hydra.Screen;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.Screen;

[TestFixture]
public class ScreenStartupTests
{
    [Test]
    public async Task StartAsync_NameNotInScreens_LogsErrorAndDoesNotCrash()
    {
        var profile = TransitionTestHelper.Profile("unknown-host", new HydraConfig
        {
            Mode = Mode.Master,
            Hosts =
            [
                new HostConfig { Name = "laptop", Neighbours = [new NeighbourConfig { Direction = Direction.Right, Name = "desktop" }] },
                new HostConfig { Name = "desktop", Neighbours = [new NeighbourConfig { Direction = Direction.Left, Name = "laptop" }] },
            ],
        });

        var logs = new CapturingLog<InputRouter>(LogLevel.Error);
        var platform = new FakePlatform();
        var service = new InputRouter(
            platform, platform, profile, new NullRelaySender(),
            new FakeScreenDetector(), NullLoggerFactory.Instance, logs, new NullScreenSaverSync(), new NullClipboardSync(),
            FileTransferService.Null(), new NullFileSelectionDetector(), new NullOsdNotification(), TransitionTestHelper.TestActivityTracker(profile));

        // must not throw
        await service.StartAsync(CancellationToken.None);

        Assert.That(logs.Entries, Is.Not.Empty, "should log an error when hostname is not in screens");
    }
}
