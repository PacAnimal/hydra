using System.Text.Json;
using Cathedral.Config;
using Cathedral.Logging;
using Hydra.Config;
using Hydra.Platform;
using Hydra.Relay;
using Hydra.Screen;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.Relay;

[TestFixture]
public class MasterSlaveProtocolTests
{
    // -- MasterRelayConnection message filtering --

    [Test]
    public async Task MasterRelay_PassesSlaveLogThrough()
    {
        var relay = new TestableMasterRelay();
        var received = new List<MessageKind>();
        relay.MessageReceived += async (_, kind, _) => { received.Add(kind); await ValueTask.CompletedTask; };

        var msg = new SlaveLogMessage(2, "cat", "hello", null);
        await relay.SimulateReceive("slave-pc", MessageKind.SlaveLog, JsonSerializer.Serialize(msg, SaneJson.Options));

        Assert.That(received, Is.EqualTo([MessageKind.SlaveLog]));
    }

    [Test]
    public async Task MasterRelay_DropsMasterConfig()
    {
        var relay = new TestableMasterRelay();
        var received = new List<MessageKind>();
        relay.MessageReceived += async (_, kind, _) => { received.Add(kind); await ValueTask.CompletedTask; };

        await relay.SimulateReceive("other-master", MessageKind.MasterConfig, "{}");

        Assert.That(received, Is.Empty);
    }

    [Test]
    public async Task MasterRelay_PassesScreenInfoThrough()
    {
        var relay = new TestableMasterRelay();
        var received = new List<MessageKind>();
        relay.MessageReceived += async (_, kind, _) => { received.Add(kind); await ValueTask.CompletedTask; };

        var info = new ScreenInfoMessage([new ScreenInfoEntry("screen:0", 0, 0, 1920, 1080, 1.0m)]);
        await relay.SimulateReceive("slave-pc", MessageKind.ScreenInfo, JsonSerializer.Serialize(info, SaneJson.Options));

        Assert.That(received, Is.EqualTo([MessageKind.ScreenInfo]));
    }

    // -- slave log end-to-end through InputRouter --

    [Test]
    public async Task SlaveLog_AppearsInMasterLoggerWithSlavePrefix()
    {
        var config = MakeConfig("slave-pc");
        var relay = new FakeRelay();
        var logs = new LogCapture();
        var service = new InputRouter(new FakePlatform(), config, relay, new FakeScreenDetector(), logs, NullLogger<InputRouter>.Instance, new NullScreenSaverSync());
        await service.StartAsync(CancellationToken.None);

        var msg = new SlaveLogMessage((int)LogLevel.Warning, "MyService", "something went wrong", null);
        await relay.FireMessageReceived("slave-pc", MessageKind.SlaveLog, JsonSerializer.Serialize(msg, SaneJson.Options));

        var (category, level, message) = logs.Entries.Single(e => e.Category.StartsWith("slave:"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(category, Is.EqualTo("slave:slave-pc/MyService"));
            Assert.That(level, Is.EqualTo(LogLevel.Warning));
            Assert.That(message, Does.Contain("something went wrong"));
        }

        await service.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task SlaveLog_WithException_IncludesExceptionInMasterLog()
    {
        var config = MakeConfig("slave-pc");
        var relay = new FakeRelay();
        var logs = new LogCapture();
        var service = new InputRouter(new FakePlatform(), config, relay, new FakeScreenDetector(), logs, NullLogger<InputRouter>.Instance, new NullScreenSaverSync());
        await service.StartAsync(CancellationToken.None);

        var msg = new SlaveLogMessage((int)LogLevel.Error, "Crasher", "boom", "System.Exception: kaboom");
        await relay.FireMessageReceived("slave-pc", MessageKind.SlaveLog, JsonSerializer.Serialize(msg, SaneJson.Options));

        var (_, _, message) = logs.Entries.Single(e => e.Category.StartsWith("slave:"));
        Assert.That(message, Does.Contain("kaboom"));

        await service.StopAsync(CancellationToken.None);
    }

    // -- InputRouter MasterConfig gating --

    [Test]
    public async Task OnPeersChanged_SendsMasterConfigOnlyToConfiguredSlaves()
    {
        var config = MakeConfig("slave-pc");
        var relay = new FakeRelay();
        var service = MakeService(config, relay);
        await service.StartAsync(CancellationToken.None);

        await relay.FirePeersChanged("slave-pc", "other-master");

        Assert.That(MasterConfigTargets(relay), Is.EqualTo(["slave-pc"]));

        await service.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task OnPeersChanged_SendsMasterConfigAgainAfterSlaveReappears()
    {
        var config = MakeConfig("slave-pc");
        var relay = new FakeRelay();
        var service = MakeService(config, relay);
        await service.StartAsync(CancellationToken.None);

        await relay.FirePeersChanged("slave-pc");
        await relay.FirePeersChanged();           // slave disappears
        relay.Sent.Clear();
        await relay.FirePeersChanged("slave-pc"); // slave reappears

        Assert.That(MasterConfigTargets(relay), Is.EqualTo(["slave-pc"]));

        await service.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task OnPeersChanged_DoesNotResendMasterConfigWhileSlaveStillConnected()
    {
        var config = MakeConfig("slave-pc");
        var relay = new FakeRelay();
        var service = MakeService(config, relay);
        await service.StartAsync(CancellationToken.None);

        await relay.FirePeersChanged("slave-pc");
        relay.Sent.Clear();
        await relay.FirePeersChanged("slave-pc", "another-peer"); // slave still present

        Assert.That(MasterConfigTargets(relay), Is.Empty);

        await service.StopAsync(CancellationToken.None);
    }

    // -- slave relay disconnect --

    [Test]
    public async Task SlaveRelay_OnDisconnected_EntersNoMasterState()
    {
        var cursor = new FakeCursorVisibility();
        var hider = new SlaveCursorHider(cursor, NullLogger<SlaveCursorHider>.Instance);
        var slave = new TestableSlaveRelay(hider);

        await slave.SimulateMasterConfig("master-pc");
        Assert.That(hider.State, Is.EqualTo(SlaveCursorState.Hidden), "pre-condition: cursor hidden when master connected");

        await slave.SimulateDisconnected();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hider.State, Is.EqualTo(SlaveCursorState.NoMaster));
            Assert.That(cursor.IsHidden, Is.False);
        }

        hider.Dispose();
    }

    [Test]
    public async Task SlaveRelay_OnDisconnected_ClearsStateForReconnect()
    {
        var cursor = new FakeCursorVisibility();
        var hider = new SlaveCursorHider(cursor, NullLogger<SlaveCursorHider>.Instance);
        var slave = new TestableSlaveRelay(hider);

        await slave.SimulateMasterConfig("master-pc");
        await slave.SimulateDisconnected();

        // master reconnects — cursor should hide again (master count goes from 0 to 1)
        await slave.SimulateMasterConfig("master-pc");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hider.State, Is.EqualTo(SlaveCursorState.Hidden));
            Assert.That(cursor.IsHidden, Is.True);
        }

        hider.Dispose();
    }

    // -- per-master log level handshake --

    [Test]
    public async Task MasterConfig_WithLogLevel_StoredInWorldState()
    {
        var state = new WorldState();
        using var slave = new TestableSlaveRelayWithState(state);

        var msg = new MasterConfigMessage(LogLevel.Debug);
        await slave.SimulateMasterConfig("master-pc", JsonSerializer.Serialize(msg, SaneJson.Options));

        var configs = await state.GetMasterConfigs();
        Assert.That(configs["master-pc"].LogLevel, Is.EqualTo(LogLevel.Debug));
    }

    [Test]
    public async Task MasterConfig_WithoutLogLevel_StoredWithNullLogLevel()
    {
        var state = new WorldState();
        using var slave = new TestableSlaveRelayWithState(state);

        await slave.SimulateMasterConfig("master-pc", "{}");

        var configs = await state.GetMasterConfigs();
        Assert.That(configs["master-pc"].LogLevel, Is.Null);
    }

    // -- SlaveLogSender per-master filtering --

    [Test]
    public async Task SlaveLogSender_FiltersEntriesByMasterLogLevel()
    {
        var state = new WorldState();
        await state.AddMaster("verbose-master", new MasterConfigMessage(LogLevel.Debug));
        await state.AddMaster("quiet-master", new MasterConfigMessage(LogLevel.Warning));

        var forwarder = new SlaveLogForwarder();
        await forwarder.ForwardAsync(new LogEntry(LogLevel.Debug, "Test", default, "debug msg", "debug msg", null));

        var relay = new FakeRelay();
        var sender = new SlaveLogSender(relay, forwarder, state, NullLogger<SlaveLogSender>.Instance);
        await sender.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        await sender.StopAsync(CancellationToken.None);

        var sent = relay.Sent.Where(s => s.Kind == MessageKind.SlaveLog).ToList();
        Assert.That(sent, Has.Count.EqualTo(1));
        Assert.That(sent[0].Targets, Is.EquivalentTo(["verbose-master"]));
    }

    [Test]
    public async Task SlaveLogSender_EntryBelowAllMasterLevels_NotSent()
    {
        var state = new WorldState();
        await state.AddMaster("master", new MasterConfigMessage(LogLevel.Warning));

        var forwarder = new SlaveLogForwarder();
        await forwarder.ForwardAsync(new LogEntry(LogLevel.Debug, "Test", default, "debug msg", "debug msg", null));

        var relay = new FakeRelay();
        var sender = new SlaveLogSender(relay, forwarder, state, NullLogger<SlaveLogSender>.Instance);
        await sender.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        await sender.StopAsync(CancellationToken.None);

        Assert.That(relay.Sent.Where(s => s.Kind == MessageKind.SlaveLog), Is.Empty);
    }

    // -- helpers --

    private static IHydraProfile MakeConfig(params string[] slaveNames) =>
        TransitionTestHelper.Profile("main", new HydraConfig
        {
            Mode = Mode.Master,
            Hosts = [new HostConfig { Name = "main" }, .. slaveNames.Select(n => new HostConfig { Name = n })],
        });

    private static InputRouter MakeService(IHydraProfile profile, IRelaySender relay) =>
        new(new FakePlatform(), profile, relay, new FakeScreenDetector(), NullLoggerFactory.Instance, NullLogger<InputRouter>.Instance, new NullScreenSaverSync());

    private static List<string> MasterConfigTargets(FakeRelay relay) =>
        [.. relay.Sent.Where(s => s.Kind == MessageKind.MasterConfig).SelectMany(s => s.Targets)];

    private sealed class TestableSlaveRelay(SlaveCursorHider hider) : SlaveRelayConnection(
        TransitionTestHelper.Profile("slave", new HydraConfig { Mode = Mode.Slave }),
        NullLogger<RelayConnection>.Instance,
        new NullPlatformOutput(),
        new FakeScreenDetector(),
        new WorldState(),
        hider,
        new NullScreenSaverSync(),
        new NullScreensaverSuppressor())
    {
        // simulates a legacy master that sends no log level
        public Task SimulateMasterConfig(string host) => OnReceive(host, MessageKind.MasterConfig, "{}");
        public Task SimulateDisconnected() => OnDisconnected();
    }

    // variant that exposes its WorldState for log-level assertions
    private sealed class TestableSlaveRelayWithState : SlaveRelayConnection, IDisposable
    {
        private readonly SlaveCursorHider _hider;

        public TestableSlaveRelayWithState(WorldState state) : this(
            state, new SlaveCursorHider(new FakeCursorVisibility(), NullLogger<SlaveCursorHider>.Instance))
        { }

        private TestableSlaveRelayWithState(WorldState state, SlaveCursorHider hider) : base(
            TransitionTestHelper.Profile("slave", new HydraConfig { Mode = Mode.Slave }),
            NullLogger<RelayConnection>.Instance,
            new NullPlatformOutput(),
            new FakeScreenDetector(),
            state,
            hider,
            new NullScreenSaverSync(),
            new NullScreensaverSuppressor())
        {
            _hider = hider;
        }

        public Task SimulateMasterConfig(string host, string json) => OnReceive(host, MessageKind.MasterConfig, json);
        public override void Dispose() { _hider.Dispose(); base.Dispose(); }
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

    private sealed class TestableMasterRelay : MasterRelayConnection
    {
        public TestableMasterRelay() : base(
            TransitionTestHelper.Profile("master", new HydraConfig { Mode = Mode.Master }),
            NullLogger<RelayConnection>.Instance,
            new WorldState())
        { }

        public Task SimulateReceive(string host, MessageKind kind, string json) => OnReceive(host, kind, json);
    }

    private sealed class LogCapture : ILoggerFactory
    {
        public readonly List<(string Category, LogLevel Level, string Message)> Entries = [];

        public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, Entries);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class CaptureLogger(string category, List<(string, LogLevel, string)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                entries.Add((category, logLevel, formatter(state, exception)));
            }
        }
    }
}
