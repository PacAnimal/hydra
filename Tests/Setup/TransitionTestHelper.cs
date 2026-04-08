using System.Text.Json;
using Cathedral.Config;
using Hydra.Config;
using Hydra.Platform;
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
        var service = new InputRouter(platform, TestConfig, relay, screens, NullLoggerFactory.Instance, NullLogger<InputRouter>.Instance, new NullScreenSaverSync());
        return new TestServiceBundle(platform, relay, service);
    }

    public static async Task BringRemoteOnline(FakeRelay relay)
    {
        await relay.FirePeersChanged("remote");
        var info = JsonSerializer.Serialize(new ScreenInfoMessage([new ScreenInfoEntry("screen:0", 0, 0, 2560, 1440, 1.0m)]), SaneJson.Options);
        await relay.FireMessageReceived("remote", MessageKind.ScreenInfo, info);
    }

    // remote-only: single remote host "mac", no local screens
    public static readonly HydraConfig RemoteOnlyConfig = new()
    {
        Mode = Mode.Master,
        Name = "pi",
        RemoteOnly = true,
        Hosts = [new HostConfig { Name = "mac", Neighbours = [] }],
    };

    public static TestServiceBundle CreateRemoteOnlyService(HydraConfig? config = null)
    {
        var platform = new FakePlatform();
        var relay = new FakeRelay();
        var screens = new FakeScreenDetector();
        var service = new InputRouter(platform, config ?? RemoteOnlyConfig, relay, screens, NullLoggerFactory.Instance, NullLogger<InputRouter>.Instance, new NullScreenSaverSync());
        return new TestServiceBundle(platform, relay, service);
    }

    public static async Task BringHostOnline(FakeRelay relay, string host, string screenName = "screen:0") =>
        await BringHostsOnline(relay, [host], [(host, screenName)]);

    // brings multiple hosts online at once — fires a single PeersChanged with all hosts, then ScreenInfo for each
    public static async Task BringHostsOnline(FakeRelay relay, string[] hosts, (string Host, string Screen)[]? screens = null)
    {
        await relay.FirePeersChanged(hosts);
        foreach (var host in hosts)
        {
            var screenName = screens?.FirstOrDefault(s => s.Host == host).Screen ?? "screen:0";
            var info = JsonSerializer.Serialize(new ScreenInfoMessage([new ScreenInfoEntry(screenName, 0, 0, 2560, 1440, 1.0m)]), SaneJson.Options);
            await relay.FireMessageReceived(host, MessageKind.ScreenInfo, info);
        }
    }
}

public record TestServiceBundle(FakePlatform Platform, FakeRelay Relay, InputRouter Service);
