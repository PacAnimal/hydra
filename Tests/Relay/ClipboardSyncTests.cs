using System.Text.Json;
using Cathedral.Config;
using Hydra.Config;
using Hydra.Platform;
using Hydra.Relay;
using Hydra.Screen;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.Relay;

[TestFixture]
public class ClipboardSyncTests
{
    // -- master pushes clipboard on screen enter --

    [Test]
    public async Task OnEnterRemoteScreen_PushesClipboardToSlave()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetText("hello from master");

        var (platform, relay, service) = CreateMasterService(clipboard);
        await service.StartAsync(CancellationToken.None);
        await TransitionTestHelper.BringRemoteOnline(relay);

        platform.FireMouseMove(2559, 720); // cross right edge → remote

        var push = relay.Sent.Where(s => s.Kind == MessageKind.ClipboardPush).ToList();
        Assert.That(push, Has.Count.EqualTo(1));
        var msg = JsonSerializer.Deserialize<ClipboardPushMessage>(push[0].Json, SaneJson.Options);
        Assert.That(msg?.Text, Is.EqualTo("hello from master"));

        await service.StopAsync(CancellationToken.None);
        platform.Dispose();
    }

    [Test]
    public async Task OnEnterRemoteScreen_EmptyClipboard_NoPushSent()
    {
        var clipboard = new FakeClipboardSync(); // GetText returns null

        var (platform, relay, service) = CreateMasterService(clipboard);
        await service.StartAsync(CancellationToken.None);
        await TransitionTestHelper.BringRemoteOnline(relay);

        platform.FireMouseMove(2559, 720);

        Assert.That(relay.Sent.Where(s => s.Kind == MessageKind.ClipboardPush), Is.Empty);

        await service.StopAsync(CancellationToken.None);
        platform.Dispose();
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

        await service.StopAsync(CancellationToken.None);
        platform.Dispose();
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

        await service.StopAsync(CancellationToken.None);
        platform.Dispose();
    }

    // -- master handles pull response --

    [Test]
    public async Task OnClipboardPullResponse_SetsLocalClipboard()
    {
        var clipboard = new FakeClipboardSync();
        var (platform, relay, service) = CreateMasterService(clipboard);
        await service.StartAsync(CancellationToken.None);
        await TransitionTestHelper.BringRemoteOnline(relay);

        var response = new ClipboardPullResponseMessage("from slave");
        await relay.FireMessageReceived("remote", MessageKind.ClipboardPullResponse,
            JsonSerializer.Serialize(response, SaneJson.Options));

        Assert.That(clipboard.Text, Is.EqualTo("from slave"));

        await service.StopAsync(CancellationToken.None);
        platform.Dispose();
    }

    [Test]
    public async Task OnClipboardPullResponse_WhenOnRemoteScreen_ForwardsToActiveHost()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetText("master text");

        var (platform, relay, service) = CreateMasterService(clipboard);
        await service.StartAsync(CancellationToken.None);
        await TransitionTestHelper.BringRemoteOnline(relay);

        platform.FireMouseMove(2559, 720); // enter remote
        relay.Sent.Clear();

        // pull response arrives while cursor is still on remote
        var response = new ClipboardPullResponseMessage("slave had this");
        await relay.FireMessageReceived("remote", MessageKind.ClipboardPullResponse,
            JsonSerializer.Serialize(response, SaneJson.Options));

        var push = relay.Sent.Where(s => s.Kind == MessageKind.ClipboardPush).ToList();
        Assert.That(push, Has.Count.EqualTo(1));
        var msg = JsonSerializer.Deserialize<ClipboardPushMessage>(push[0].Json, SaneJson.Options);
        Assert.That(msg?.Text, Is.EqualTo("slave had this"));

        await service.StopAsync(CancellationToken.None);
        platform.Dispose();
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

        platform.FireMouseMove(2559, 720); // cross right edge → remote

        var push = relay.Sent.Where(s => s.Kind == MessageKind.ClipboardPush).ToList();
        Assert.That(push, Has.Count.EqualTo(1));
        var msg = JsonSerializer.Deserialize<ClipboardPushMessage>(push[0].Json, SaneJson.Options);
        Assert.That(msg?.PrimaryText, Is.EqualTo("primary text"));

        await service.StopAsync(CancellationToken.None);
        platform.Dispose();
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

        var push = relay.Sent.Where(s => s.Kind == MessageKind.ClipboardPush).ToList();
        Assert.That(push, Has.Count.EqualTo(1));
        var msg = JsonSerializer.Deserialize<ClipboardPushMessage>(push[0].Json, SaneJson.Options);
        Assert.That(msg?.PrimaryText, Is.EqualTo("primary text"));

        await service.StopAsync(CancellationToken.None);
        platform.Dispose();
    }

    [Test]
    public async Task OnClipboardPullResponse_SetsPrimaryText()
    {
        var clipboard = new FakeClipboardSync();
        var (platform, relay, service) = CreateMasterService(clipboard);
        await service.StartAsync(CancellationToken.None);
        await BringRemoteOnlineWithPlatform(relay, PeerPlatform.Linux);

        var response = new ClipboardPullResponseMessage("from slave", "primary from slave");
        await relay.FireMessageReceived("remote", MessageKind.ClipboardPullResponse,
            JsonSerializer.Serialize(response, SaneJson.Options));

        Assert.That(clipboard.PrimaryText, Is.EqualTo("primary from slave"));

        await service.StopAsync(CancellationToken.None);
        platform.Dispose();
    }

    [Test]
    public async Task OnClipboardPullResponse_ForwardsPrimaryToLinuxSlave()
    {
        // master has no local PRIMARY (GetPrimaryText returns null) but receives it from slave A;
        // cursor is still on that slave so _lastReceivedPrimaryText should be forwarded in the push
        var clipboard = new FakeClipboardSync();
        clipboard.SetText("master clipboard");
        // no SetPrimaryText — simulates a non-Linux master

        var (platform, relay, service) = CreateMasterService(clipboard);
        await service.StartAsync(CancellationToken.None);
        await BringRemoteOnlineWithPlatform(relay, PeerPlatform.Linux);

        platform.FireMouseMove(2559, 720); // enter remote
        relay.Sent.Clear();

        // pull response arrives while cursor is still on the Linux slave
        var response = new ClipboardPullResponseMessage("slave clipboard", "highlighted text");
        await relay.FireMessageReceived("remote", MessageKind.ClipboardPullResponse,
            JsonSerializer.Serialize(response, SaneJson.Options));

        var push = relay.Sent.Where(s => s.Kind == MessageKind.ClipboardPush).ToList();
        Assert.That(push, Has.Count.EqualTo(1));
        var msg = JsonSerializer.Deserialize<ClipboardPushMessage>(push[0].Json, SaneJson.Options);
        Assert.That(msg?.PrimaryText, Is.EqualTo("highlighted text"));

        await service.StopAsync(CancellationToken.None);
        platform.Dispose();
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

        var push = relay.Sent.Where(s => s.Kind == MessageKind.ClipboardPush).ToList();
        Assert.That(push, Has.Count.EqualTo(1));
        var msg = JsonSerializer.Deserialize<ClipboardPushMessage>(push[0].Json, SaneJson.Options);
        Assert.That(msg?.ImagePng, Is.EqualTo(png));

        await service.StopAsync(CancellationToken.None);
        platform.Dispose();
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

        Assert.That(relay.Sent.Where(s => s.Kind == MessageKind.ClipboardPush), Is.Empty);

        await service.StopAsync(CancellationToken.None);
        platform.Dispose();
    }

    [Test]
    public async Task OnEnterRemoteScreen_ImageAndText_BothInSamePush()
    {
        var png = MakeFakePng();
        var clipboard = new FakeClipboardSync();
        clipboard.SetText("alt text");
        clipboard.SetupImage(png);

        var (platform, relay, service) = CreateMasterService(clipboard);
        await service.StartAsync(CancellationToken.None);
        await TransitionTestHelper.BringRemoteOnline(relay);

        platform.FireMouseMove(2559, 720);

        var push = relay.Sent.Where(s => s.Kind == MessageKind.ClipboardPush).ToList();
        Assert.That(push, Has.Count.EqualTo(1));
        var msg = JsonSerializer.Deserialize<ClipboardPushMessage>(push[0].Json, SaneJson.Options);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(msg?.Text, Is.EqualTo("alt text"));
            Assert.That(msg?.ImagePng, Is.EqualTo(png));
        }

        await service.StopAsync(CancellationToken.None);
        platform.Dispose();
    }

    [Test]
    public async Task OnClipboardPullResponse_SetsLocalImage()
    {
        var png = MakeFakePng();
        var clipboard = new FakeClipboardSync();
        var (platform, relay, service) = CreateMasterService(clipboard);
        await service.StartAsync(CancellationToken.None);
        await TransitionTestHelper.BringRemoteOnline(relay);

        var response = new ClipboardPullResponseMessage(null, null, png);
        await relay.FireMessageReceived("remote", MessageKind.ClipboardPullResponse,
            JsonSerializer.Serialize(response, SaneJson.Options));

        Assert.That(clipboard.ImagePng, Is.EqualTo(png));

        await service.StopAsync(CancellationToken.None);
        platform.Dispose();
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

    private static (FakePlatform Platform, FakeRelay Relay, InputRouter Service) CreateMasterService(IClipboardSync clipboard)
    {
        var platform = new FakePlatform();
        var relay = new FakeRelay();
        var service = new InputRouter(
            platform, TransitionTestHelper.TestConfig, relay,
            new FakeScreenDetector(), NullLoggerFactory.Instance, NullLogger<InputRouter>.Instance,
            new NullScreenSaverSync(), clipboard);
        return (platform, relay, service);
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

    private static TestableSlaveWithClipboard MakeTestableSlaveRelay(IClipboardSync clipboard)
    {
        var hider = new SlaveCursorHider(new FakeCursorVisibility(), NullLogger<SlaveCursorHider>.Instance);
        return new TestableSlaveWithClipboard(hider, clipboard);
    }

    private sealed class TestableSlaveWithClipboard(SlaveCursorHider hider, IClipboardSync clipboard) : SlaveRelayConnection(
        TransitionTestHelper.Profile("slave", new HydraConfig { Mode = Mode.Slave }),
        NullLogger<RelayConnection>.Instance,
        new NullPlatformOutput(),
        new FakeScreenDetector(),
        new WorldState(),
        hider,
        new NullScreenSaverSync(),
        new NullScreensaverSuppressor(),
        clipboard)
    {
        public Task SimulateReceive(string host, MessageKind kind, string json) => OnReceive(host, kind, json);
    }

}
