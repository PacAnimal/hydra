using System.Net;
using System.Net.Sockets;
using Hydra.Config;
using Hydra.Relay;
using Microsoft.Extensions.Logging;
using Tests.Setup;

namespace Tests.Relay;

/// <summary>
/// A client beside its own embedded relay holds its first connect until that relay listens, so it never spends a
/// ReconnectDelay on a refused connect; a client with no embedded relay connects at once. Every dial is recorded and
/// parked, so a test sees each attempt and none fails behind its back.
/// </summary>
[TestFixture]
public class EmbeddedRelayReadyTests
{
    private static readonly TimeSpan ReadyCap = TimeSpan.FromSeconds(Constants.EmbeddedReadyTimeoutSeconds);

    [Test]
    public async Task EmbeddedRelay_FirstConnectWaitsUntilReady()
    {
        var ready = new FakeRelayReady();
        await using var relay = await ReadyProbe.Running(ready);

        await relay.Clock.Started.Next("the ready cap");
        Assert.That(relay.Attempts.Count, Is.Zero, "nothing dials before the relay is ready");

        ready.Ready.SetResult();
        await relay.Attempts.Next("the first connect");

        Assert.That(relay.Logged.Messages, Has.None.Contains("retrying"));
    }

    [Test]
    public async Task NoEmbeddedRelay_ConnectsWithoutWaiting()
    {
        await using var relay = await ReadyProbe.Running(null);

        await relay.Attempts.Next("the first connect");

        Assert.That(relay.Clock.Started.Count, Is.Zero, "no ready wait was started");
    }

    [Test]
    public async Task CancelDuringWait_EndsPromptly()
    {
        await using var relay = await ReadyProbe.Running(new FakeRelayReady());
        await relay.Clock.Started.Next("the ready cap");

        await relay.Cancel();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await Settles(relay.Run), Is.True);
            Assert.That(relay.Run.IsCanceled, Is.True);
            Assert.That(relay.Attempts.Count, Is.Zero);
            Assert.That(relay.Logged.Entries.Count(e => e.Level == LogLevel.Warning), Is.Zero);
        }
    }

    [Test]
    public async Task RelayNeverReady_ConnectsAfterCapWithOneWarning()
    {
        await using var relay = await ReadyProbe.Running(new FakeRelayReady());
        await relay.Clock.Started.Next("the ready cap");

        relay.Clock.Advance(ReadyCap);
        await relay.Attempts.Next("the first connect");

        Assert.That(relay.Logged.Entries.Count(e => e.Level == LogLevel.Warning), Is.EqualTo(1));
    }

    private sealed class FakeRelayReady : IRelayReady
    {
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task WaitForReady() => Ready.Task;
    }

    private sealed class ReadyProbe : RelayConnection, IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancel = new();

        private ReadyProbe(string networkConfig, IRelayReady? ready, CapturingLog<RelayConnection> log)
            : base(TransitionTestHelper.Profile("ready-probe", new HydraConfig { Mode = Mode.Master, NetworkConfig = networkConfig }),
                log, new WorldState(), ready)
        {
            Logged = log;
        }

        // the port is never dialled: every connect parks in ConnectRelaySocket
        internal static async Task<ReadyProbe> Running(IRelayReady? ready)
        {
            var blob = await Common.NetworkConfig.ComputeEmbeddedBlob("https://localhost:1", "ready-test-password");
            var probe = new ReadyProbe(blob, ready, new CapturingLog<RelayConnection>());
            probe.Run = Task.Run(() => probe.Execute(probe._cancel.Token));
            return probe;
        }

        internal CapturingLog<RelayConnection> Logged { get; }
        internal StartedTimers Clock { get; } = new();
        internal NotificationQueue<DnsEndPoint> Attempts { get; } = new();
        internal Task Run { get; private set; } = Task.CompletedTask;

        protected override TimeProvider ReadyClock => Clock;

        protected override async Task<Socket> ConnectRelaySocket(DnsEndPoint target, CancellationToken cancel)
        {
            Attempts.Push(target);
            await Task.Delay(Timeout.Infinite, cancel);
            throw new OperationCanceledException(cancel);
        }

        internal Task Cancel() => _cancel.CancelAsync();

        public async ValueTask DisposeAsync()
        {
            await _cancel.CancelAsync();
            await Settles(Run);
            _cancel.Dispose();
            Dispose();
        }
    }
}
