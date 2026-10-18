using Hydra.Config;
using Hydra.Screen;
using Microsoft.Extensions.Logging;
using Tests.Setup;

namespace Tests.Screen;

[TestFixture]
public class ScreenStartupTests
{
    private readonly StartedServices _services = new();

    [TearDown]
    public Task TearDown() => _services.StopAll();

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

        // must not throw
        await _services.Start(TransitionTestHelper.CreateService(profile: profile, log: logs));

        Assert.That(logs.Entries, Is.Not.Empty, "should log an error when hostname is not in screens");
    }
}
