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
    public async Task OnEnterRemoteScreen_OversizedClipboard_NoPushSent()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetText(new string('x', 1_000_001));

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
    public async Task SlaveReceivesClipboardPull_CallsGetText()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetText("slave content");

        var slave = MakeTestableSlaveRelay(clipboard);
        var before = clipboard.GetTextCallCount;

        await slave.SimulateReceive("master-pc", MessageKind.ClipboardPull, "{}");

        Assert.That(clipboard.GetTextCallCount, Is.GreaterThan(before));
    }

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

    private sealed class NullPlatformOutput : IPlatformOutput
    {
        public void MoveMouse(int x, int y) { }
        public void MoveMouseRelative(int dx, int dy) { }
        public void InjectKey(KeyEventMessage msg) { }
        public void InjectMouseButton(MouseButtonMessage msg) { }
        public void InjectMouseScroll(MouseScrollMessage msg) { }
        public void Dispose() { }
    }

    private sealed class NullScreensaverSuppressor : IScreensaverSuppressor
    {
        public void Suppress() { }
        public void Restore() { }
    }

    // ReSharper disable UnusedAutoPropertyAccessor.Local
    private sealed class FakeCursorVisibility : ICursorVisibility
    {
        public bool IsHidden { get; private set; }
        public void HideCursor() => IsHidden = true;
        public void ShowCursor() => IsHidden = false;
        public CursorPosition GetCursorPosition() => new(0, 0);
    }
    // ReSharper restore UnusedAutoPropertyAccessor.Local
}
