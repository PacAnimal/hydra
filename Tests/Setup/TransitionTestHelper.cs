using System.Text.Json;
using Cathedral.Config;
using Hydra.Config;
using Hydra.FileTransfer;
using Hydra.Platform;
using Hydra.Relay;
using Hydra.Screen;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Setup;

public static class TransitionTestHelper
{
    // convenience: wraps a named HydraConfig into an IHydraProfile for use in tests
    public static IHydraProfile Profile(string name, HydraConfig? config = null) =>
        new HydraProfile(new HydraConfigFile { Name = name }, config);

    // "home" is the local screen; "remote" is a real remote host
    public static readonly IHydraProfile TestConfig = ProfileWith();

    // the standard two-host master, with room for the one setting a test wants to vary
    public static IHydraProfile ProfileWith(int? maxMouseHz = null) => Profile("home", new HydraConfig
    {
        Mode = Mode.Master,
        MaxMouseHz = maxMouseHz,
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
    });

    /// <param name="getTickCount">The router's clock, for a test that needs to drive time rather than wait for it.</param>
    /// <param name="activityTracker">Substituted where a test asserts on what the router reported as activity.</param>
    /// <param name="world">
    /// The world the router records peers in. Pass one to READ what the router wrote — what a peer
    /// advertised is only observable there, and nothing else in the process can see it.
    /// </param>
    /// <param name="timeProvider">
    /// Where the router's timers come from. Pass a <c>FakeTimeProvider</c> to fire the mouse-batch flush
    /// on demand rather than racing the few milliseconds it is armed for.
    /// </param>
    /// <param name="profile">The master profile to run, for a test that varies a configured setting.</param>
    /// <param name="selectionDetector">The local file manager, for a test of the copy hotkey.</param>
    /// <param name="osd">Where on-screen messages go, for a test that reads them.</param>
    /// <param name="fileTransfer">The copy buffer's owner, for a test that reads what a copy left in it.</param>
    public static TestServiceBundle CreateService(Func<long>? getTickCount = null, IActivityTracker? activityTracker = null, IWorldState? world = null,
        TimeProvider? timeProvider = null, IHydraProfile? profile = null, IFileSelectionDetector? selectionDetector = null, IOsdNotification? osd = null,
        FileTransferService? fileTransfer = null)
    {
        var platform = new FakePlatform();
        var relay = new FakeRelay();
        var screens = new FakeScreenDetector();
        var config = profile ?? TestConfig;
        var tracker = activityTracker ?? new ActivityTracker(config, new Lazy<IRelaySender>(() => relay), new WorldState(), new NullScreenSaverSync(), NullLogger<ActivityTracker>.Instance);
        var service = new InputRouter(platform, platform, config, relay, screens, NullLoggerFactory.Instance, NullLogger<InputRouter>.Instance, new NullScreenSaverSync(), new NullClipboardSync(),
            fileTransfer ?? FileTransferService.Null(), selectionDetector ?? new NullFileSelectionDetector(), osd ?? new NullOsdNotification(), tracker, peerState: world,
            getTickCount: getTickCount, timeProvider: timeProvider);
        platform.AfterFireCallback = service.FlushAsync;
        return new TestServiceBundle(platform, relay, service);
    }

    // home → remote → remote2, for crossings between two remote hosts
    public static readonly IHydraProfile ChainConfig = Profile("home", new HydraConfig
    {
        Mode = Mode.Master,
        Hosts =
        [
            new HostConfig { Name = "home", Neighbours = [new NeighbourConfig { Direction = Direction.Right, Name = "remote" }] },
            new HostConfig
            {
                Name = "remote",
                Neighbours =
                [
                    new NeighbourConfig { Direction = Direction.Left, Name = "home" },
                    new NeighbourConfig { Direction = Direction.Right, Name = "remote2" },
                ],
            },
            new HostConfig { Name = "remote2", Neighbours = [new NeighbourConfig { Direction = Direction.Left, Name = "remote" }] },
        ],
    });

    public static async Task BringRemoteOnline(FakeRelay relay)
    {
        await relay.FirePeersChanged("remote");
        var info = JsonSerializer.Serialize(new ScreenInfoMessage([new ScreenInfoEntry("screen:0", 0, 0, 2560, 1440, 1.0m)]), SaneJson.Options);
        await relay.FireMessageReceived("remote", MessageKind.ScreenInfo, info);
    }

    // remote-only: single remote host "mac", no local screens
    public static readonly IHydraProfile RemoteOnlyConfig = Profile("pi", new HydraConfig
    {
        Mode = Mode.Master,
        RemoteOnly = true,
        Hosts = [new HostConfig { Name = "mac", Neighbours = [] }],
    });

    // remote-only: "mac" on the left, "win" on the right
    public static readonly IHydraProfile RemoteOnlyPairConfig = Profile("pi", new HydraConfig
    {
        Mode = Mode.Master,
        RemoteOnly = true,
        Hosts =
        [
            new HostConfig { Name = "mac", Neighbours = [new NeighbourConfig { Direction = Direction.Right, Name = "win" }] },
            new HostConfig { Name = "win", Neighbours = [new NeighbourConfig { Direction = Direction.Left, Name = "mac" }] },
        ],
    });

    /// <param name="config">The master profile to run.</param>
    /// <param name="headless">No local screen at all, which is what turns the lock hotkey into confinement.</param>
    /// <param name="getTickCount">The router's clock, for a test that needs to drive time rather than wait for it.</param>
    public static TestServiceBundle CreateRemoteOnlyService(IHydraProfile? config = null, bool headless = false, Func<long>? getTickCount = null)
    {
        var platform = new FakePlatform();
        var relay = new FakeRelay();
        var screens = new FakeScreenDetector();
        if (headless)
            screens.Snapshot = new LocalScreenSnapshot([], []);
        var profile = config ?? RemoteOnlyConfig;
        var tracker = new ActivityTracker(profile, new Lazy<IRelaySender>(() => relay), new WorldState(), new NullScreenSaverSync(), NullLogger<ActivityTracker>.Instance);
        var service = new InputRouter(platform, platform, profile, relay, screens, NullLoggerFactory.Instance, NullLogger<InputRouter>.Instance, new NullScreenSaverSync(), new NullClipboardSync(),
            FileTransferService.Null(), new NullFileSelectionDetector(), new NullOsdNotification(), tracker, getTickCount: getTickCount);
        platform.AfterFireCallback = service.FlushAsync;
        return new TestServiceBundle(platform, relay, service);
    }

    // advertises one host's monitors, laid out left to right
    public static async Task AdvertiseScreens(FakeRelay relay, string host, params string[] screenNames)
    {
        var entries = screenNames.Select((name, i) => new ScreenInfoEntry(name, i * 2560, 0, 2560, 1440, 1.0m)).ToList();
        var info = JsonSerializer.Serialize(new ScreenInfoMessage(entries), SaneJson.Options);
        await relay.FireMessageReceived(host, MessageKind.ScreenInfo, info);
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

    public static ActivityTracker TestActivityTracker(IHydraProfile? profile = null) => new(
        profile ?? TestConfig,
        new Lazy<IRelaySender>(() => new NullRelaySender()),
        new WorldState(),
        new NullScreenSaverSync(),
        NullLogger<ActivityTracker>.Instance);
}

public record TestServiceBundle(FakePlatform Platform, FakeRelay Relay, InputRouter Service);
