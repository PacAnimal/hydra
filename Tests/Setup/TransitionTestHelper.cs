using System.Text.Json;
using Cathedral.Config;
using Hydra.Config;
using Hydra.Relay;
using Hydra.Screen;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Setup;

public static class TransitionTestHelper
{
    // "home" is the local screen; "remote" is a real remote host
    public static readonly HydraConfig TestConfig = new()
    {
        Mode = Mode.Master,
        Name = "home",
        Hosts =
        [
            new HostConfig
            {
                Name = "home",
                Neighbours = [new NeighbourConfig { Direction = Direction.Right, Name = "remote" }],
            },
            new HostConfig
            {
                Name = "remote",
                Neighbours = [new NeighbourConfig { Direction = Direction.Left, Name = "home" }],
            },
        ],
    };

    public static TestServiceBundle CreateService()
    {
        var platform = new FakePlatform();
        var relay = new FakeRelay();
        var screens = new FakeScreenDetector();
        var service = new ScreenTransitionService(platform, TestConfig, relay, screens, NullLoggerFactory.Instance, NullLogger<ScreenTransitionService>.Instance);
        return new TestServiceBundle(platform, relay, service);
    }

    public static void BringRemoteOnline(FakeRelay relay)
    {
        relay.FirePeersChanged("remote");
        var info = JsonSerializer.Serialize(new ScreenInfoMessage([new ScreenInfoEntry("screen:0", 0, 0, 2560, 1440, 1.0m)]), SaneJson.Options);
        relay.FireMessageReceived("remote", MessageKind.ScreenInfo, info);
    }
}

public record TestServiceBundle(FakePlatform Platform, FakeRelay Relay, ScreenTransitionService Service);
