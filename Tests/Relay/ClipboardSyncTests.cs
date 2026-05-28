using System.Text.Json;
using Cathedral.Config;
using Hydra.FileTransfer;
using Hydra.Platform;
using Hydra.Relay;
using Hydra.Screen;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.Relay;

[TestFixture]
public class ClipboardSyncTests
{
    private FakePlatform? _platform;
    private InputRouter? _service;

    [TearDown]
    public async Task TearDown()
    {
        if (_service != null)
        {
            await _service.StopAsync(CancellationToken.None);
            _service = null;
        }
        if (_platform != null) await _platform.DisposeAsync();
        _platform = null;
    }

    // -- master pushes clipboard on screen enter --

    [Test]
    public async Task OnEnterRemoteScreen_PushesClipboardToSlave()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetText("hello from master");

        var (platform, relay, service) = CreateMasterService(clipboard);
        await service.StartAsync(CancellationToken.None);
        await TransitionTestHelper.BringRemoteOnline(relay);

        platform.FireMouseMove(2559, 720); // cross right edge → sends clipboard hash
        Assert.That(relay.Sent.Any(s => s.Kind == MessageKind.ClipboardHash), Is.True);

        await SimulatePullRequest(relay); // slave sees different hash, requests push

        var push = relay.Sent.Where(s => s.Kind == MessageKind.ClipboardPush).ToList();
        Assert.That(push, Has.Count.EqualTo(1));
        var msg = JsonSerializer.Deserialize<ClipboardPushMessage>(push[0].Json, SaneJson.Options);
        Assert.That(msg?.Text, Is.EqualTo("hello from master"));

    }

    [Test]
    public async Task OnEnterRemoteScreen_EmptyClipboard_NoPushSent()
    {
        var clipboard = new FakeClipboardSync(); // GetText returns null

        var (platform, relay, service) = CreateMasterService(clipboard);
        await service.StartAsync(CancellationToken.None);
        await TransitionTestHelper.BringRemoteOnline(relay);

        platform.FireMouseMove(2559, 720);

        using (Assert.EnterMultipleScope())
        {
            // empty clipboard → no hash query and no push
            Assert.That(relay.Sent.Where(s => s.Kind == MessageKind.ClipboardHash), Is.Empty);
            Assert.That(relay.Sent.Where(s => s.Kind == MessageKind.ClipboardPush), Is.Empty);
        }

    }

    [Test]
    public async Task OnEnterRemoteScreen_OversizedText_NoPushSent()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetText(new string('x', 16 * 1024 * 1024 + 1)); // > 16 MiB UTF-8

        var (platform, relay, service) = CreateMasterService(clipboard);
        await service.StartAsync(CancellationToken.None);
        await TransitionTestHelper.BringRemoteOnline(relay);

        platform.FireMouseMove(2559, 720);

        Assert.That(relay.Sent.Where(s => s.Kind == MessageKind.ClipboardPush), Is.Empty);

    }

    // -- master pulls clipboard on screen leave (return to local) --

    [Test]
    public async Task OnLeaveRemoteScreen_PullsSentToSlave()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetText("something");

        var (platform, relay, service) = CreateMasterService(clipboard);
        await service.StartAsync(CancellationToken.None);
        await TransitionTestHelper.BringRemoteOnline(relay);

        platform.FireMouseMove(2559, 720); // enter remote
        relay.Sent.Clear();

        // simulate post-warp artifact (big jump dropped by bogus filter), then a real small move back
        platform.FireMouseMove(1280, 720); // warp artifact — dropped
        platform.FireMouseMove(1275, 720); // dx=-5 → cursor exits left edge of remote → return to local

        Assert.That(relay.Sent.Any(s => s.Kind == MessageKind.ClipboardPull), Is.True);

    }

    // -- master handles pull response --

    [Test]
    public async Task OnClipboardPullResponse_SetsLocalClipboard()
    {
        var clipboard = new FakeClipboardSync();
        var (platform, relay, service) = CreateMasterService(clipboard);
        await service.StartAsync(CancellationToken.None);
        await TransitionTestHelper.BringRemoteOnline(relay);

        // trigger leave so _lastPulledFrom = "remote"
        platform.FireMouseMove(2559, 720);
        platform.FireMouseMove(1280, 720); // warp artifact
        platform.FireMouseMove(1275, 720); // leave remote → pull → _lastPulledFrom = "remote"

        var response = new ClipboardPullResponseMessage("from slave");
        await relay.FireMessageReceived("remote", MessageKind.ClipboardPullResponse,
            JsonSerializer.Serialize(response, SaneJson.Options));

        Assert.That(clipboard.Text, Is.EqualTo("from slave"));

    }

    [Test]
    public async Task OnClipboardPullResponse_WhenOnRemoteScreen_ForwardsToActiveHost()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetText("master text");

        var (platform, relay, service) = CreateMasterService(clipboard);
        await service.StartAsync(CancellationToken.None);
        await TransitionTestHelper.BringRemoteOnline(relay);

        // enter → leave (sets _lastPulledFrom) → re-enter so cursor is on remote
        platform.FireMouseMove(2559, 720);
        platform.FireMouseMove(1280, 720); // warp artifact
        platform.FireMouseMove(1275, 720); // leave remote → _lastPulledFrom = "remote"
        platform.FireMouseMove(2559, 720); // re-enter remote
        relay.Sent.Clear();

        // pull response arrives while cursor is still on remote → forwards via hash query
        var response = new ClipboardPullResponseMessage("slave had this");
        await relay.FireMessageReceived("remote", MessageKind.ClipboardPullResponse,
            JsonSerializer.Serialize(response, SaneJson.Options));

        Assert.That(relay.Sent.Any(s => s.Kind == MessageKind.ClipboardHash), Is.True);
        await SimulatePullRequest(relay); // slave sees different hash, requests push

        var push = relay.Sent.Where(s => s.Kind == MessageKind.ClipboardPush).ToList();
        Assert.That(push, Has.Count.EqualTo(1));
        var msg = JsonSerializer.Deserialize<ClipboardPushMessage>(push[0].Json, SaneJson.Options);
        Assert.That(msg?.Text, Is.EqualTo("slave had this"));

    }

    // -- PRIMARY selection: master push to Linux vs non-Linux peers --

    [Test]
    public async Task OnEnterLinuxSlave_PushesPrimaryText()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetText("clipboard text");
        clipboard.SetPrimaryText("primary text");

        var (platform, relay, service) = CreateMasterService(clipboard);
        await service.StartAsync(CancellationToken.None);
        await BringRemoteOnlineWithPlatform(relay, PeerPlatform.Linux);

        platform.FireMouseMove(2559, 720); // cross right edge → hash query
        await SimulatePullRequest(relay);

        var push = relay.Sent.Where(s => s.Kind == MessageKind.ClipboardPush).ToList();
        Assert.That(push, Has.Count.EqualTo(1));
        var msg = JsonSerializer.Deserialize<ClipboardPushMessage>(push[0].Json, SaneJson.Options);
        Assert.That(msg?.PrimaryText, Is.EqualTo("primary text"));

    }

    [Test]
    public async Task OnEnterNonLinuxSlave_StillIncludesPrimaryText()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetText("clipboard text");
        clipboard.SetPrimaryText("primary text");

        var (platform, relay, service) = CreateMasterService(clipboard);
        await service.StartAsync(CancellationToken.None);
        await BringRemoteOnlineWithPlatform(relay, PeerPlatform.Windows);

        platform.FireMouseMove(2559, 720);
        await SimulatePullRequest(relay);

        var push = relay.Sent.Where(s => s.Kind == MessageKind.ClipboardPush).ToList();
        Assert.That(push, Has.Count.EqualTo(1));
        var msg = JsonSerializer.Deserialize<ClipboardPushMessage>(push[0].Json, SaneJson.Options);
        Assert.That(msg?.PrimaryText, Is.EqualTo("primary text"));

    }

    [Test]
    public async Task OnClipboardPullResponse_SetsPrimaryText()
    {
        var clipboard = new FakeClipboardSync();
        var (platform, relay, service) = CreateMasterService(clipboard);
        await service.StartAsync(CancellationToken.None);
        await BringRemoteOnlineWithPlatform(relay, PeerPlatform.Linux);

        // trigger leave so _lastPulledFrom = "remote"
        platform.FireMouseMove(2559, 720);
        platform.FireMouseMove(1280, 720); // warp artifact
        platform.FireMouseMove(1275, 720); // leave remote → _lastPulledFrom = "remote"

        var response = new ClipboardPullResponseMessage("from slave", "primary from slave");
        await relay.FireMessageReceived("remote", MessageKind.ClipboardPullResponse,
            JsonSerializer.Serialize(response, SaneJson.Options));

        Assert.That(clipboard.PrimaryText, Is.EqualTo("primary from slave"));

    }

    [Test]
    public async Task OnClipboardPullResponse_ForwardsPrimaryToLinuxSlave()
    {
        // master has no local PRIMARY (GetPrimaryText returns null) but receives it from slave A;
        // cursor is still on that slave so primary text from pull response should be forwarded in the push
        var clipboard = new FakeClipboardSync();
        clipboard.SetText("master clipboard");
        // no SetPrimaryText — simulates a non-Linux master

        var (platform, relay, service) = CreateMasterService(clipboard);
        await service.StartAsync(CancellationToken.None);
        await BringRemoteOnlineWithPlatform(relay, PeerPlatform.Linux);

        // enter → leave (sets _lastPulledFrom) → re-enter so cursor is on remote
        platform.FireMouseMove(2559, 720);
        platform.FireMouseMove(1280, 720); // warp artifact
        platform.FireMouseMove(1275, 720); // leave remote → _lastPulledFrom = "remote"
        platform.FireMouseMove(2559, 720); // re-enter remote
        relay.Sent.Clear();

        // pull response arrives while cursor is still on the Linux slave → forward via hash query
        var response = new ClipboardPullResponseMessage("slave clipboard", "highlighted text");
        await relay.FireMessageReceived("remote", MessageKind.ClipboardPullResponse,
            JsonSerializer.Serialize(response, SaneJson.Options));

        await SimulatePullRequest(relay); // slave sees different hash, requests push

        var push = relay.Sent.Where(s => s.Kind == MessageKind.ClipboardPush).ToList();
        Assert.That(push, Has.Count.EqualTo(1));
        var msg = JsonSerializer.Deserialize<ClipboardPushMessage>(push[0].Json, SaneJson.Options);
        Assert.That(msg?.PrimaryText, Is.EqualTo("highlighted text"));

    }

    // -- slave receives push --

    [Test]
    public async Task SlaveReceivesClipboardPush_SetsLocalClipboard()
    {
        var clipboard = new FakeClipboardSync();
        var slave = MakeTestableSlaveRelay(clipboard);

        var push = new ClipboardPushMessage("pushed text");
        await slave.SimulateReceive("master-pc", MessageKind.ClipboardPush,
            JsonSerializer.Serialize(push, SaneJson.Options));

        Assert.That(clipboard.Text, Is.EqualTo("pushed text"));
    }

    [Test]
    public async Task SlaveReceivesClipboardPush_SetsPrimaryText()
    {
        var clipboard = new FakeClipboardSync();
        var slave = MakeTestableSlaveRelay(clipboard);

        var push = new ClipboardPushMessage("text", "highlighted selection");
        await slave.SimulateReceive("master-pc", MessageKind.ClipboardPush,
            JsonSerializer.Serialize(push, SaneJson.Options));

        Assert.That(clipboard.PrimaryText, Is.EqualTo("highlighted selection"));
    }

    [Test]
    public async Task SlaveReceivesClipboardPull_CallsGetPrimaryText()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetPrimaryText("selected on slave");

        var slave = MakeTestableSlaveRelay(clipboard);
        var before = clipboard.GetPrimaryTextCallCount;

        await slave.SimulateReceive("master-pc", MessageKind.ClipboardPull, "{}");

        Assert.That(clipboard.GetPrimaryTextCallCount, Is.GreaterThan(before));
    }

    [Test]
    public async Task SlaveReceivesClipboardPull_CallsGetText()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetText("slave content");

        var slave = MakeTestableSlaveRelay(clipboard);
        var before = clipboard.GetTextCallCount;

        await slave.SimulateReceive("master-pc", MessageKind.ClipboardPull, "{}");

        Assert.That(clipboard.GetTextCallCount, Is.GreaterThan(before));
    }

    // -- image clipboard sync --

    [Test]
    public async Task OnEnterRemoteScreen_PushesImageToSlave()
    {
        var png = MakeFakePng();
        var clipboard = new FakeClipboardSync();
        clipboard.SetupImage(png);

        var (platform, relay, service) = CreateMasterService(clipboard);
        await service.StartAsync(CancellationToken.None);
        await TransitionTestHelper.BringRemoteOnline(relay);

        platform.FireMouseMove(2559, 720);
        await SimulatePullRequest(relay);

        var push = relay.Sent.Where(s => s.Kind == MessageKind.ClipboardPush).ToList();
        Assert.That(push, Has.Count.EqualTo(1));
        var msg = JsonSerializer.Deserialize<ClipboardPushMessage>(push[0].Json, SaneJson.Options);
        Assert.That(msg?.ImagePng, Is.EqualTo(png));

    }

    [Test]
    public async Task OnEnterRemoteScreen_OversizedImage_NoPushSent()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupImage(new byte[16 * 1024 * 1024 + 1]); // > 16 MiB

        var (platform, relay, service) = CreateMasterService(clipboard);
        await service.StartAsync(CancellationToken.None);
        await TransitionTestHelper.BringRemoteOnline(relay);

        platform.FireMouseMove(2559, 720);

        using (Assert.EnterMultipleScope())
        {
            // oversized image dropped during trim → nothing to push, no hash query sent
            Assert.That(relay.Sent.Where(s => s.Kind == MessageKind.ClipboardHash), Is.Empty);
            Assert.That(relay.Sent.Where(s => s.Kind == MessageKind.ClipboardPush), Is.Empty);
        }

    }

    [Test]
    public async Task OnEnterRemoteScreen_ImageAndText_ImageWins()
    {
        // when both text and image are on the clipboard, image wins — text is just a fallback representation
        var png = MakeFakePng();
        var clipboard = new FakeClipboardSync();
        clipboard.SetText("alt text");
        clipboard.SetupImage(png);

        var (platform, relay, service) = CreateMasterService(clipboard);
        await service.StartAsync(CancellationToken.None);
        await TransitionTestHelper.BringRemoteOnline(relay);

        platform.FireMouseMove(2559, 720);
        await SimulatePullRequest(relay);

        var push = relay.Sent.Where(s => s.Kind == MessageKind.ClipboardPush).ToList();
        Assert.That(push, Has.Count.EqualTo(1));
        var msg = JsonSerializer.Deserialize<ClipboardPushMessage>(push[0].Json, SaneJson.Options);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(msg?.Text, Is.Empty);
            Assert.That(msg?.ImagePng, Is.EqualTo(png));
        }

    }

    [Test]
    public async Task OnClipboardPullResponse_SetsLocalImage()
    {
        var png = MakeFakePng();
        var clipboard = new FakeClipboardSync();
        var (platform, relay, service) = CreateMasterService(clipboard);
        await service.StartAsync(CancellationToken.None);
        await TransitionTestHelper.BringRemoteOnline(relay);

        // trigger leave so _lastPulledFrom = "remote"
        platform.FireMouseMove(2559, 720);
        platform.FireMouseMove(1280, 720); // warp artifact
        platform.FireMouseMove(1275, 720); // leave remote → _lastPulledFrom = "remote"

        var response = new ClipboardPullResponseMessage(null, null, png);
        await relay.FireMessageReceived("remote", MessageKind.ClipboardPullResponse,
            JsonSerializer.Serialize(response, SaneJson.Options));

        Assert.That(clipboard.ImagePng, Is.EqualTo(png));

    }

    [Test]
    public async Task SlaveReceivesClipboardPush_SetsImage()
    {
        var png = MakeFakePng();
        var clipboard = new FakeClipboardSync();
        var slave = MakeTestableSlaveRelay(clipboard);

        var push = new ClipboardPushMessage("", null, png);
        await slave.SimulateReceive("master-pc", MessageKind.ClipboardPush,
            JsonSerializer.Serialize(push, SaneJson.Options));

        Assert.That(clipboard.ImagePng, Is.EqualTo(png));
    }

    [Test]
    public async Task SlaveReceivesClipboardPull_CallsGetImagePng()
    {
        var png = MakeFakePng();
        var clipboard = new FakeClipboardSync();
        clipboard.SetupImage(png);

        var slave = MakeTestableSlaveRelay(clipboard);
        var before = clipboard.GetImagePngCallCount;

        await slave.SimulateReceive("master-pc", MessageKind.ClipboardPull, "{}");

        Assert.That(clipboard.GetImagePngCallCount, Is.GreaterThan(before));
    }

    // -- clipboard hash exchange: master sends its hash on enter, slave decides to pull --

    [Test]
    public async Task OnEnterRemoteScreen_SendsClipboardHashNotPushDirectly()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetText("some text");

        var (platform, relay, service) = CreateMasterService(clipboard);
        await service.StartAsync(CancellationToken.None);
        await TransitionTestHelper.BringRemoteOnline(relay);

        platform.FireMouseMove(2559, 720);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(relay.Sent.Any(s => s.Kind == MessageKind.ClipboardHash), Is.True);
            Assert.That(relay.Sent.Where(s => s.Kind == MessageKind.ClipboardPush), Is.Empty);
        }
    }

    [Test]
    public async Task OnEnterRemoteScreen_ClipboardHashCarriesMasterHash()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetText("master content");

        var (platform, relay, service) = CreateMasterService(clipboard);
        await service.StartAsync(CancellationToken.None);
        await TransitionTestHelper.BringRemoteOnline(relay);

        platform.FireMouseMove(2559, 720);

        var hashMsg = relay.Sent.Where(s => s.Kind == MessageKind.ClipboardHash).ToList();
        Assert.That(hashMsg, Has.Count.EqualTo(1));
        var msg = JsonSerializer.Deserialize<ClipboardHashMessage>(hashMsg[0].Json, SaneJson.Options);
        var expected = ClipboardUtils.ClipboardHash(new ClipboardSnapshot("master content", null, null));
        Assert.That(msg?.Hash, Is.EqualTo(expected));
    }

    [Test]
    public async Task SlaveReceivesClipboardHash_HashDiffers_SendsPullRequest()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetText("slave has something else");
        var slave = MakeTestableSlaveRelay(clipboard);

        // master sends a hash that won't match slave's clipboard
        var hashMsg = new ClipboardHashMessage(0UL);
        await slave.SimulateReceive("master-pc", MessageKind.ClipboardHash,
            JsonSerializer.Serialize(hashMsg, SaneJson.Options));

        Assert.That(slave.Sent.Any(s => s.Kind == MessageKind.ClipboardPullRequest), Is.True);
    }

    [Test]
    public async Task SlaveReceivesClipboardHash_HashMatches_NoResponse()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetText("identical content");
        var slave = MakeTestableSlaveRelay(clipboard);

        var matchingHash = ClipboardUtils.ClipboardHash(new ClipboardSnapshot("identical content", null, null));
        var hashMsg = new ClipboardHashMessage(matchingHash);
        await slave.SimulateReceive("master-pc", MessageKind.ClipboardHash,
            JsonSerializer.Serialize(hashMsg, SaneJson.Options));

        Assert.That(slave.Sent.Where(s => s.Kind == MessageKind.ClipboardPullRequest), Is.Empty);
    }

    [Test]
    public async Task OnClipboardPullRequest_SendsFullPush()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetText("master has this");

        var (platform, relay, service) = CreateMasterService(clipboard);
        await service.StartAsync(CancellationToken.None);
        await TransitionTestHelper.BringRemoteOnline(relay);

        platform.FireMouseMove(2559, 720); // enter remote (guard: cursor must be on remote)
        await SimulatePullRequest(relay);

        var push = relay.Sent.Where(s => s.Kind == MessageKind.ClipboardPush).ToList();
        Assert.That(push, Has.Count.EqualTo(1));
        var msg = JsonSerializer.Deserialize<ClipboardPushMessage>(push[0].Json, SaneJson.Options);
        Assert.That(msg?.Text, Is.EqualTo("master has this"));
    }

    // -- pull: slave skips full response when hashes match --

    [Test]
    public async Task SlaveReceivesClipboardPull_HashMatches_SendsUnchangedResponse()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetText("same text");
        var slave = MakeTestableSlaveRelay(clipboard);

        var slaveHash = ClipboardUtils.ClipboardHash(new ClipboardSnapshot("same text", null, null));
        var pull = new ClipboardPullMessage(slaveHash); // master sends slave's own hash
        await slave.SimulateReceive("master-pc", MessageKind.ClipboardPull,
            JsonSerializer.Serialize(pull, SaneJson.Options));

        var resp = slave.Sent.Where(s => s.Kind == MessageKind.ClipboardPullResponse).ToList();
        Assert.That(resp, Has.Count.EqualTo(1));
        var msg = JsonSerializer.Deserialize<ClipboardPullResponseMessage>(resp[0].Json, SaneJson.Options);
        Assert.That(msg?.Unchanged, Is.True);
    }

    [Test]
    public async Task SlaveReceivesClipboardPull_HashDiffers_SendsFullResponse()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetText("slave has this");
        var slave = MakeTestableSlaveRelay(clipboard);

        var pull = new ClipboardPullMessage(0UL); // hash won't match
        await slave.SimulateReceive("master-pc", MessageKind.ClipboardPull,
            JsonSerializer.Serialize(pull, SaneJson.Options));

        var resp = slave.Sent.Where(s => s.Kind == MessageKind.ClipboardPullResponse).ToList();
        Assert.That(resp, Has.Count.EqualTo(1));
        var msg = JsonSerializer.Deserialize<ClipboardPullResponseMessage>(resp[0].Json, SaneJson.Options);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(msg?.Text, Is.EqualTo("slave has this"));
            Assert.That(msg?.Unchanged, Is.Not.True);
        }
    }

    // -- security guards --

    [Test]
    public async Task OnClipboardPullRequest_CursorNotOnThatScreen_Ignored()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetText("master has this");

        var (_, relay, service) = CreateMasterService(clipboard);
        await service.StartAsync(CancellationToken.None);
        await TransitionTestHelper.BringRemoteOnline(relay);

        // cursor never moved to remote — guard should block the push
        await SimulatePullRequest(relay);

        Assert.That(relay.Sent.Where(s => s.Kind == MessageKind.ClipboardPush), Is.Empty);
    }

    [Test]
    public async Task OnClipboardPullResponse_NotFromLastPulledHost_Ignored()
    {
        var clipboard = new FakeClipboardSync();
        var (platform, relay, service) = CreateMasterService(clipboard);
        await service.StartAsync(CancellationToken.None);
        await TransitionTestHelper.BringRemoteOnline(relay);

        // trigger leave from "remote" → _lastPulledFrom = "remote"
        platform.FireMouseMove(2559, 720);
        platform.FireMouseMove(1280, 720); // warp artifact
        platform.FireMouseMove(1275, 720); // leave remote

        // response arrives from a different host — guard should block SetClipboard
        var before = clipboard.SetClipboardCallCount;
        var response = new ClipboardPullResponseMessage("from attacker");
        await relay.FireMessageReceived("other-host", MessageKind.ClipboardPullResponse,
            JsonSerializer.Serialize(response, SaneJson.Options));

        Assert.That(clipboard.SetClipboardCallCount, Is.EqualTo(before));
    }

    [Test]
    public async Task OnClipboardPullResponse_Unchanged_SkipsSetClipboard()
    {
        var clipboard = new FakeClipboardSync();
        var (platform, relay, service) = CreateMasterService(clipboard);
        await service.StartAsync(CancellationToken.None);
        await TransitionTestHelper.BringRemoteOnline(relay);

        // trigger leave so _lastPulledFrom = "remote" (guard passes)
        platform.FireMouseMove(2559, 720);
        platform.FireMouseMove(1280, 720); // warp artifact
        platform.FireMouseMove(1275, 720); // leave remote → _lastPulledFrom = "remote"

        var before = clipboard.SetClipboardCallCount;
        var response = new ClipboardPullResponseMessage(null, Unchanged: true);
        await relay.FireMessageReceived("remote", MessageKind.ClipboardPullResponse,
            JsonSerializer.Serialize(response, SaneJson.Options));

        Assert.That(clipboard.SetClipboardCallCount, Is.EqualTo(before));
    }

    // minimal valid-ish PNG bytes (just needs to be non-null and distinguishable)
    private static byte[] MakeFakePng() =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, // PNG signature
        0x00, 0x00, 0x00, 0x0D,                          // IHDR length
        0x49, 0x48, 0x44, 0x52,                          // "IHDR"
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, // 1x1 pixel
        0x08, 0x02, 0x00, 0x00, 0x00,                    // 8bit RGB
        0x90, 0x77, 0x53, 0xDE,                          // CRC
    ];

    // -- helpers --

    private (FakePlatform Platform, FakeRelay Relay, InputRouter Service) CreateMasterService(IClipboardSync clipboard)
    {
        _platform = new FakePlatform();
        var relay = new FakeRelay();
        _service = new InputRouter(
            _platform, _platform, TransitionTestHelper.TestConfig, relay,
            new FakeScreenDetector(), NullLoggerFactory.Instance, NullLogger<InputRouter>.Instance,
            new NullScreenSaverSync(), clipboard,
            FileTransferService.Null(), new NullFileSelectionDetector(), new NullOsdNotification(), TransitionTestHelper.TestActivityTracker());
        _platform.AfterFireCallback = _service.FlushAsync;
        return (_platform, relay, _service);
    }

    // brings "remote" online and records its platform so the master knows what to push
    private static async Task BringRemoteOnlineWithPlatform(FakeRelay relay, PeerPlatform platform)
    {
        await relay.FirePeersChanged("remote");
        var info = JsonSerializer.Serialize(
            new ScreenInfoMessage([new ScreenInfoEntry("screen:0", 0, 0, 2560, 1440, 1.0m)], platform),
            SaneJson.Options);
        await relay.FireMessageReceived("remote", MessageKind.ScreenInfo, info);
    }

    private static TestableSlaveRelay MakeTestableSlaveRelay(IClipboardSync clipboard) =>
        new(clipboard: clipboard);

    // simulate slave deciding its hash differs from master's and requesting a full push
    private static async Task SimulatePullRequest(FakeRelay relay, string host = "remote")
        => await relay.FireMessageReceived(host, MessageKind.ClipboardPullRequest,
            JsonSerializer.Serialize(new ClipboardPullRequestMessage(), SaneJson.Options));

}
