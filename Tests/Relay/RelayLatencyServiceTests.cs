using Cathedral.Extensions;
using Hydra.Relay;
using Microsoft.Extensions.Time.Testing;
using Tests.Setup;

namespace Tests.Relay;

[TestFixture]
public class RelayLatencyServiceTests
{
    private static readonly string[] RemoteOnly = ["remote"];
    private readonly StartedServices _services = new();

    [TearDown]
    public Task TearDown() => _services.StopAll();

    [Test]
    public async Task PeerProbe_ResponseRecordsRtt()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(100));
        var relay = new FakeRelay();
        var service = await _services.Start(new RelayLatencyService(relay, () => clock.GetUtcNow().ToUnixTimeMilliseconds()));

        await relay.FirePeersChanged("remote");
        var (_, _, json) = relay.Snapshot().Single(item => item.Kind == MessageKind.LatencyProbe);
        var probe = json.FromSaneJson<LatencyProbeMessage>()!;

        clock.Advance(TimeSpan.FromMilliseconds(37));
        await relay.FireMessageReceived("remote", MessageKind.LatencyProbeResponse,
            new LatencyProbeResponseMessage(probe.Sequence).ToSaneJson());

        var result = service.GetSnapshot().Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Host, Is.EqualTo("remote"));
            Assert.That(result.LastRttMs, Is.EqualTo(37));
            Assert.That(result.AverageRttMs, Is.EqualTo(37));
            Assert.That(result.Samples, Is.EqualTo(1));
            Assert.That(result.Lost, Is.Zero);
        }
    }

    [Test]
    public async Task IncomingProbe_IsEchoedToSource()
    {
        var relay = new FakeRelay();
        _ = await _services.Start(new RelayLatencyService(relay));

        await relay.FireMessageReceived("remote", MessageKind.LatencyProbe,
            new LatencyProbeMessage(42).ToSaneJson());

        var (targets, kind, json) = relay.Snapshot().Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(targets, Is.EqualTo(RemoteOnly));
            Assert.That(kind, Is.EqualTo(MessageKind.LatencyProbeResponse));
            Assert.That(json.FromSaneJson<LatencyProbeResponseMessage>()!.Sequence, Is.EqualTo(42));
        }
    }

    [Test]
    public async Task TimedOutProbe_IsCountedAndPendingStateStaysBounded()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(100));
        var relay = new FakeRelay();
        var service = await _services.Start(new RelayLatencyService(relay, () => clock.GetUtcNow().ToUnixTimeMilliseconds()));
        await relay.FirePeersChanged("remote");

        clock.Advance(TimeSpan.FromMilliseconds(5_001));
        service.SendProbes();

        var result = service.GetSnapshot().Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Samples, Is.Zero);
            Assert.That(result.Lost, Is.EqualTo(1));
            Assert.That(relay.Snapshot().Count(item => item.Kind == MessageKind.LatencyProbe), Is.EqualTo(2));
        }
    }

    [Test]
    public async Task MalformedProbe_IsIgnored()
    {
        var relay = new FakeRelay();
        await _services.Start(new RelayLatencyService(relay));

        Assert.That(async () => await relay.FireMessageReceived("remote", MessageKind.LatencyProbe,
            "{"), Throws.Nothing);
        Assert.That(relay.Snapshot(), Is.Empty);
    }
}
