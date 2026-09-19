using Cathedral.Extensions;
using Hydra.Relay;
using Tests.Setup;

namespace Tests.Relay;

[TestFixture]
public class RelayLatencyServiceTests
{
    private static readonly string[] RemoteOnly = ["remote"];

    [Test]
    public async Task PeerProbe_ResponseRecordsRtt()
    {
        long now = 100;
        var relay = new FakeRelay();
        // now is mutated below to advance the fake clock the service reads through this closure.
        // ReSharper disable once AccessToModifiedClosure
        var service = new RelayLatencyService(relay, () => now);
        await service.StartAsync(CancellationToken.None);

        await relay.FirePeersChanged("remote");
        var (_, _, json) = relay.Sent.Single(item => item.Kind == MessageKind.LatencyProbe);
        var probe = json.FromSaneJson<LatencyProbeMessage>()!;

        now = 137;
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

        await service.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task IncomingProbe_IsEchoedToSource()
    {
        var relay = new FakeRelay();
        var service = new RelayLatencyService(relay);
        await service.StartAsync(CancellationToken.None);

        await relay.FireMessageReceived("remote", MessageKind.LatencyProbe,
            new LatencyProbeMessage(42).ToSaneJson());

        var (targets, kind, json) = relay.Sent.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(targets, Is.EqualTo(RemoteOnly));
            Assert.That(kind, Is.EqualTo(MessageKind.LatencyProbeResponse));
            Assert.That(json.FromSaneJson<LatencyProbeResponseMessage>()!.Sequence, Is.EqualTo(42));
        }

        await service.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task TimedOutProbe_IsCountedAndPendingStateStaysBounded()
    {
        long now = 100;
        var relay = new FakeRelay();
        // now is mutated below to advance the fake clock the service reads through this closure.
        // ReSharper disable once AccessToModifiedClosure
        var service = new RelayLatencyService(relay, () => now);
        await service.StartAsync(CancellationToken.None);
        await relay.FirePeersChanged("remote");

        now = 5_101;
        service.SendProbes();

        var result = service.GetSnapshot().Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Samples, Is.Zero);
            Assert.That(result.Lost, Is.EqualTo(1));
            Assert.That(relay.Sent.Count(item => item.Kind == MessageKind.LatencyProbe), Is.EqualTo(2));
        }

        await service.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task MalformedProbe_IsIgnored()
    {
        var relay = new FakeRelay();
        var service = new RelayLatencyService(relay);
        await service.StartAsync(CancellationToken.None);

        Assert.That(async () => await relay.FireMessageReceived("remote", MessageKind.LatencyProbe,
            "{"), Throws.Nothing);
        Assert.That(relay.Sent, Is.Empty);

        await service.StopAsync(CancellationToken.None);
    }
}
