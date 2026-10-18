using System.Text.Json;
using Cathedral.Config;
using Hydra.Config;
using Hydra.FileTransfer;
using Hydra.Platform;
using Hydra.Relay;
using Hydra.Screen;
using Microsoft.Extensions.Logging;
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
    public static IHydraProfile ProfileWith(int? maxMouseHz = null, bool hideCursor = false, Dictionary<string, List<string>>? hotkeys = null,
        ClipboardSyncMode clipboardSync = ClipboardSyncMode.Hydra) =>
        Profile("home", new HydraConfig
        {
            Mode = Mode.Master,
            MaxMouseHz = maxMouseHz,
            HideCursor = hideCursor,
            Hotkeys = hotkeys,
            ClipboardSync = clipboardSync,
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
    /// <param name="screenSaverSync">The screensaver monitor, for a test that fires its activation.</param>
    /// <param name="screens">The local screen detector, for a test that changes the screens while running.</param>
    /// <param name="log">The router's log, for a test that reads what it logged.</param>
    /// <param name="clipboard">The local clipboard, for a test of clipboard sync.</param>
    /// <param name="localPlatform">The platform the router believes it runs on, rather than the one the test does.</param>
    /// <param name="loggerFactory">Where the router's other loggers come from, for a test that reads what a peer logged through it.</param>
    public static TestServiceBundle CreateService(Func<long>? getTickCount = null, IActivityTracker? activityTracker = null, IWorldState? world = null,
        TimeProvider? timeProvider = null, IHydraProfile? profile = null, IFileSelectionDetector? selectionDetector = null, IOsdNotification? osd = null,
        FileTransferService? fileTransfer = null, IScreenSaverSync? screenSaverSync = null, FakeScreenDetector? screens = null, ILogger<InputRouter>? log = null,
        IClipboardSync? clipboard = null, PeerPlatform? localPlatform = null, ILoggerFactory? loggerFactory = null)
    {
        var platform = new FakePlatform();
        var relay = new FakeRelay();
        screens ??= new FakeScreenDetector();
        var config = profile ?? TestConfig;
        var tracker = activityTracker ?? new ActivityTracker(config, new Lazy<IRelaySender>(() => relay), new WorldState(), new NullScreenSaverSync(), NullLogger<ActivityTracker>.Instance);
        var service = new InputRouter(platform, platform, config, relay, screens, loggerFactory ?? NullLoggerFactory.Instance, log ?? NullLogger<InputRouter>.Instance, screenSaverSync ?? new NullScreenSaverSync(), clipboard ?? new NullClipboardSync(),
            fileTransfer ?? FileTransferService.Null(), selectionDetector ?? new NullFileSelectionDetector(), osd ?? new NullOsdNotification(), tracker, peerState: world,
            getTickCount: getTickCount, timeProvider: timeProvider, localPlatform: localPlatform);
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

    public static async Task BringRemoteOnline(FakeRelay relay) => await BringHostOnline(relay, "remote");

    // starts the router with hosts online, the standard remote unless named
    public static Task<TestServiceBundle> StartOnline(TestServiceBundle bundle, params string[] hosts) => StartOnline(bundle, PeerPlatform.Linux, hosts);

    // as above, the hosts advertising the given platform
    public static async Task<TestServiceBundle> StartOnline(TestServiceBundle bundle, PeerPlatform platform, params string[] hosts)
    {
        await bundle.Service.StartAsync(CancellationToken.None);
        await BringHostsOnline(bundle.Relay, hosts.Length > 0 ? hosts : ["remote"], platform);
        return bundle;
    }

    // moves the cursor across the standard master's right edge onto its remote
    public static void EnterRemote(FakePlatform platform)
    {
        platform.FireMouseMove(2559, 720);
        Assert.That(platform.IsOnVirtualScreen, Is.True, "pre-condition: on the remote screen");
    }

    // started as StartOnline, with the cursor on the remote screen
    public static async Task<TestServiceBundle> Visiting(TestServiceBundle bundle, params string[] hosts)
    {
        await StartOnline(bundle, hosts);
        EnterRemote(bundle.Platform);
        return bundle;
    }

    public static async Task Stop(TestServiceBundle bundle)
    {
        await bundle.Service.StopAsync(CancellationToken.None);
        await bundle.Platform.DisposeAsync();
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
    /// <param name="screens">The local screen detector, for a test that changes the screens while running.</param>
    /// <param name="screenSaverSync">The screensaver monitor, for a test that fires its activation.</param>
    public static TestServiceBundle CreateRemoteOnlyService(IHydraProfile? config = null, bool headless = false, Func<long>? getTickCount = null,
        FakeScreenDetector? screens = null, IScreenSaverSync? screenSaverSync = null)
    {
        screens ??= new FakeScreenDetector();
        if (headless)
            screens.Snapshot = new LocalScreenSnapshot([], []);
        return CreateService(getTickCount, profile: config ?? RemoteOnlyConfig, screenSaverSync: screenSaverSync, screens: screens);
    }

    // advertises one host's monitors, laid out left to right and named as the host itself names them
    public static async Task AdvertiseScreens(FakeRelay relay, string host, int count, PeerPlatform platform = PeerPlatform.Linux)
    {
        var entries = Enumerable.Range(0, count).Select(i => new ScreenInfoEntry(ScreenNaming.BuildScreenName(host, i, count), i * 2560, 0, 2560, 1440, 1.0m)).ToList();
        var info = JsonSerializer.Serialize(new ScreenInfoMessage(entries, platform), SaneJson.Options);
        await relay.FireMessageReceived(host, MessageKind.ScreenInfo, info);
    }

    public static async Task BringHostOnline(FakeRelay relay, string host) => await BringHostsOnline(relay, [host]);

    // brings multiple hosts online at once — fires a single PeersChanged with all hosts, then one monitor's ScreenInfo for each
    public static async Task BringHostsOnline(FakeRelay relay, string[] hosts, PeerPlatform platform = PeerPlatform.Linux)
    {
        await relay.FirePeersChanged(hosts);
        foreach (var host in hosts)
            await AdvertiseScreens(relay, host, 1, platform);
    }
}

public record TestServiceBundle(FakePlatform Platform, FakeRelay Relay, InputRouter Service);
