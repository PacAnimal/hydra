using System.Text.Json;
using Cathedral.Config;
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
        relay.MessageReceived += (_, kind, _) => received.Add(kind);

        var msg = new SlaveLogMessage(2, "cat", "hello", null);
        await relay.SimulateReceive("slave-pc", MessageKind.SlaveLog, JsonSerializer.Serialize(msg, SaneJson.Options));

        Assert.That(received, Is.EqualTo([MessageKind.SlaveLog]));
    }

    [Test]
    public async Task MasterRelay_DropsMasterConfig()
    {
        var relay = new TestableMasterRelay();
        var received = new List<MessageKind>();
        relay.MessageReceived += (_, kind, _) => received.Add(kind);

        await relay.SimulateReceive("other-master", MessageKind.MasterConfig, "{}");

        Assert.That(received, Is.Empty);
    }

    [Test]
    public async Task MasterRelay_PassesScreenInfoThrough()
    {
        var relay = new TestableMasterRelay();
        var received = new List<MessageKind>();
        relay.MessageReceived += (_, kind, _) => received.Add(kind);

        var info = new ScreenInfoMessage([new ScreenInfoEntry("screen:0", 0, 0, 1920, 1080, 1.0m)]);
        await relay.SimulateReceive("slave-pc", MessageKind.ScreenInfo, JsonSerializer.Serialize(info, SaneJson.Options));

        Assert.That(received, Is.EqualTo([MessageKind.ScreenInfo]));
    }

    // -- slave log end-to-end through ScreenTransitionService --

    [Test]
    public async Task SlaveLog_AppearsInMasterLoggerWithSlavePrefix()
    {
        var config = MakeConfig("slave-pc");
        var relay = new FakeRelay();
        var logs = new LogCapture();
        var service = new ScreenTransitionService(new FakePlatform(), config, relay, new FakeScreenDetector(), logs, NullLogger<ScreenTransitionService>.Instance, new NullScreenSaverSync());
        await service.StartAsync(CancellationToken.None);

        var msg = new SlaveLogMessage((int)LogLevel.Warning, "MyService", "something went wrong", null);
        relay.FireMessageReceived("slave-pc", MessageKind.SlaveLog, JsonSerializer.Serialize(msg, SaneJson.Options));

        var (Category, Level, Message) = logs.Entries.Single(e => e.Category.StartsWith("slave:"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Category, Is.EqualTo("slave:slave-pc/MyService"));
            Assert.That(Level, Is.EqualTo(LogLevel.Warning));
            Assert.That(Message, Does.Contain("something went wrong"));
        }

        await service.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task SlaveLog_WithException_IncludesExceptionInMasterLog()
    {
        var config = MakeConfig("slave-pc");
        var relay = new FakeRelay();
        var logs = new LogCapture();
        var service = new ScreenTransitionService(new FakePlatform(), config, relay, new FakeScreenDetector(), logs, NullLogger<ScreenTransitionService>.Instance, new NullScreenSaverSync());
        await service.StartAsync(CancellationToken.None);

        var msg = new SlaveLogMessage((int)LogLevel.Error, "Crasher", "boom", "System.Exception: kaboom");
        relay.FireMessageReceived("slave-pc", MessageKind.SlaveLog, JsonSerializer.Serialize(msg, SaneJson.Options));

        var (Category, Level, Message) = logs.Entries.Single(e => e.Category.StartsWith("slave:"));
        Assert.That(Message, Does.Contain("kaboom"));

        await service.StopAsync(CancellationToken.None);
    }

    // -- ScreenTransitionService MasterConfig gating --

    [Test]
    public async Task OnPeersChanged_SendsMasterConfigOnlyToConfiguredSlaves()
    {
        var config = MakeConfig("slave-pc");
        var relay = new FakeRelay();
        var service = MakeService(config, relay);
        await service.StartAsync(CancellationToken.None);

        relay.FirePeersChanged("slave-pc", "other-master");

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

        relay.FirePeersChanged("slave-pc");
        relay.FirePeersChanged();           // slave disappears
        relay.Sent.Clear();
        relay.FirePeersChanged("slave-pc"); // slave reappears

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

        relay.FirePeersChanged("slave-pc");
        relay.Sent.Clear();
        relay.FirePeersChanged("slave-pc", "another-peer"); // slave still present

        Assert.That(MasterConfigTargets(relay), Is.Empty);

        await service.StopAsync(CancellationToken.None);
    }

    // -- helpers --

    private static HydraConfig MakeConfig(params string[] slaveNames) => new()
    {
        Mode = Mode.Master,
        Name = "main",
        Hosts = [new HostConfig { Name = "main" }, .. slaveNames.Select(n => new HostConfig { Name = n })],
    };

    private static ScreenTransitionService MakeService(HydraConfig config, IRelaySender relay) =>
        new(new FakePlatform(), config, relay, new FakeScreenDetector(), NullLoggerFactory.Instance, NullLogger<ScreenTransitionService>.Instance, new NullScreenSaverSync());

    private static List<string> MasterConfigTargets(FakeRelay relay) =>
        [.. relay.Sent.Where(s => s.Kind == MessageKind.MasterConfig).SelectMany(s => s.Targets)];

    private sealed class TestableMasterRelay : MasterRelayConnection
    {
        public TestableMasterRelay() : base(
            new HydraConfig { Mode = Mode.Master },
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
