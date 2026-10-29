using Cathedral.Extensions;
using Hydra.Config;
using Hydra.Platform;
using Hydra.Relay;
using Hydra.Screen;
using Microsoft.Extensions.Logging;
using static Tests.Setup.ClipboardStreaming;

namespace Tests.Setup;

// a router test of the master's clipboard handshake with its slaves
public abstract class MasterClipboardFixture : ServicesFixture
{
    // a master with "remote" online, advertising remotePlatform
    private protected async Task<TestServiceBundle> StartMaster(IClipboardSync clipboard, PeerPlatform remotePlatform = PeerPlatform.Linux,
        IHydraProfile? profile = null, PeerPlatform? localPlatform = null)
    {
        var bundle = TransitionTestHelper.CreateService(profile: profile, clipboard: clipboard, localPlatform: localPlatform);
        return await Services.StartOnline(bundle, remotePlatform);
    }

    // onto the remote and back off its left edge, which pulls from it; the middle move is the warp artifact, dropped
    private protected static void VisitAndLeaveRemote(FakePlatform platform)
    {
        TransitionTestHelper.EnterRemote(platform);
        LeaveRemote(platform);
    }

    // off the remote's left edge, the cursor already on it; the first move is the warp artifact, dropped
    private protected static void LeaveRemote(FakePlatform platform)
    {
        platform.FireMouseMove(1280, 720);
        platform.FireMouseMove(1275, 720);
    }

    // visits and leaves, which pulls, then goes back onto the remote with nothing sent yet, the entry's hash read
    // finished first; the pull's id
    private protected static async Task<int> PullThenReenter(TestServiceBundle bundle)
    {
        VisitAndLeaveRemote(bundle.Platform);
        var pullId = PullIdTo(bundle.Relay);
        TransitionTestHelper.EnterRemote(bundle.Platform);
        await bundle.Service.HashReads();
        bundle.Relay.Sent.Clear();
        return pullId;
    }

    private protected static List<ClipboardPushMessage> Pushes(FakeRelay relay) => relay.Sent.Of<ClipboardPushMessage>(MessageKind.ClipboardPush);

    private protected static Task AnswerPull(FakeRelay relay, ClipboardPullResponseMessage response, string host = "remote") =>
        relay.FireMessageReceived(host, MessageKind.ClipboardPullResponse, response.ToSaneJson());

    // the latest pull to host, answered unchanged: a host with nothing new
    private protected static Task AnswerUnchanged(FakeRelay relay, string host) =>
        AnswerPull(relay, new ClipboardPullResponseMessage(PullIdTo(relay, host), null, Unchanged: true), host);

    // the slave deciding its hash differs from the master's, and asking for a full push; it names the visit of the last
    // hash it was sent (or none, sent before the relay's record was cleared)
    private protected static Task SimulatePullRequest(FakeRelay relay, string host = "remote") =>
        relay.FireMessageReceived(host, MessageKind.ClipboardPullRequest,
            new ClipboardPullRequestMessage(relay.Sent.Of<ClipboardHashMessage>(MessageKind.ClipboardHash, host).LastOrDefault()?.Visit).ToSaneJson());

    // a host between left and right
    private protected static HostConfig Middle(string name, string left, string right) => new()
    {
        Name = name,
        Neighbours =
        [
            new NeighbourConfig { Direction = Direction.Left, Name = left },
            new NeighbourConfig { Direction = Direction.Right, Name = right },
        ],
    };

    // remote2 ← home → remote: the master's screen between two hosts
    private protected static readonly IHydraProfile SidesConfig = TransitionTestHelper.Profile("home", new HydraConfig
    {
        Mode = Mode.Master,
        Hosts =
        [
            Middle("home", "remote2", "remote"),
            new HostConfig { Name = "remote", Neighbours = [new NeighbourConfig { Direction = Direction.Left, Name = "home" }] },
            new HostConfig { Name = "remote2", Neighbours = [new NeighbourConfig { Direction = Direction.Right, Name = "home" }] },
        ],
    });

    // from the master's screen across its left edge onto remote2, its entries'th entry
    private protected static void EnterLeftHost(FakePlatform platform, FakeRelay relay, int entries = 1)
    {
        platform.FireMouseMove(0, 720);
        Assert.That(relay.Sent.Of(MessageKind.EnterScreen, "remote2"), Has.Count.EqualTo(entries), "pre-condition: on remote2");
    }

    // from the middle of the master's screen across its left edge onto remote2, its entries'th entry
    private protected static void EnterLeftHostFromTheMiddle(FakePlatform platform, FakeRelay relay, int entries = 1)
    {
        platform.FireMouseMove(1280, 720);
        EnterLeftHost(platform, relay, entries);
    }

    // from remote2 back across its right edge onto the master's screen
    private protected static void LeaveLeftHost(FakePlatform platform)
    {
        platform.FireMouseDelta(100, 0);
        Assert.That(platform.IsOnVirtualScreen, Is.False, "pre-condition: back on the master's screen");
    }

    // remote2 ← home → remote, both online; flushActorOnly has the router's fire callback flush the actor alone, never
    // waiting on a parked read
    private protected async Task<TestServiceBundle> StartSides(IClipboardSync clipboard, TimeProvider? clock = null, IOsdNotification? osd = null,
        IScreenSaverSync? screensaver = null, ILogger<InputRouter>? log = null, bool flushActorOnly = false)
    {
        var bundle = TransitionTestHelper.CreateService(profile: SidesConfig, clipboard: clipboard, timeProvider: clock, osd: osd,
            screenSaverSync: screensaver, log: log);
        if (flushActorOnly) bundle.Platform.AfterFireCallback = bundle.Service.Flush;
        return await Services.StartOnline(bundle, "remote", "remote2");
    }

    // home → remote → remote2, both online
    private protected Task<TestServiceBundle> StartChain(IClipboardSync clipboard, TimeProvider? clock = null) =>
        Services.StartOnline(TransitionTestHelper.CreateService(profile: TransitionTestHelper.ChainConfig, clipboard: clipboard, timeProvider: clock),
            "remote", "remote2");

    private protected static List<SentMessage> SentTo(FakeRelay relay, string host) => [.. relay.Sent.Snapshot().Where(s => s.Targets.Contains(host))];

    // remote's answer streaming in (Begin and a chunk, stream 1) when the cursor goes onto remote2 and back, which pulls
    // from remote2 too: remote's stream
    private protected static async Task<StreamedClipboard> StreamFromRemoteThenVisitTheLeftHost(FakePlatform platform, FakeRelay relay, byte[] image)
    {
        VisitAndLeaveRemote(platform);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), null, ImagePng: image));
        await Begin(relay, stream);
        await Chunk(relay, stream.Chunks[0]);
        EnterLeftHost(platform, relay);
        LeaveLeftHost(platform);
        Assert.That(relay.Sent.Of(MessageKind.ClipboardPull, "remote2"), Has.Count.EqualTo(1), "pre-condition: pulled from remote2");
        return stream;
    }
}
