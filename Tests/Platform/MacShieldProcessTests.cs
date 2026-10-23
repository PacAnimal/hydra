using System.Collections.Concurrent;
using System.IO.Pipelines;
using Hydra.Platform.MacOs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Tests.Platform;

// the shield's lifecycle against fake shields: pipes standing in for its stdin and stdout
[TestFixture]
public class MacShieldProcessTests
{
    private static readonly int[] BackedOff = [500, 1000];
    private static readonly int[] NotBackedOff = [500, 500];

    // a fake shield: a peer channel on its pipes, ended by Kill as a dying process ends them
    private sealed class FakeShield : ShieldInstance
    {
        private readonly Pipe _toHydra = new();
        private readonly Pipe _toShield = new();
        private readonly Stream _input;
        public readonly ShieldChannel Peer;
        public readonly TaskCompletionSource Killed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeShield(bool stallsWrites = false)
        {
            _input = stallsWrites ? new StalledStream() : _toShield.Writer.AsStream();
            Peer = new ShieldChannel(_toShield.Reader.AsStream(), _toHydra.Writer.AsStream(), NullLogger.Instance);
        }

        public override Stream Output => _toHydra.Reader.AsStream();
        public override Stream Input => _input;
        public override TextReader? Errors => null;

        // the shield exits: its stdout ends
        public void Exit()
        {
            if (Interlocked.Exchange(ref _exited, 1) == 0) _toHydra.Writer.Complete();
        }

        private int _exited;

        public override void Kill()
        {
            Killed.TrySetResult();
            Exit();
        }

        public override void Dispose() { }

        // what the shield is sent, acking each state as the real one does
        public async Task AckStates()
        {
            await foreach (var message in Peer.ReadAll(CancellationToken.None))
                if (message.Kind == "shield" && message.GetLong("seq") is { } seq)
                    await Peer.Send(new ShieldMessage("ack", new Dictionary<string, object?> { ["seq"] = seq }), CancellationToken.None);
        }
    }

    // a pipe a shield stopped reading: writes never return, whatever the token says
    private sealed class StalledStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(new TaskCompletionSource().Task);
    }

    // shields handed out in turn, then none
    private sealed class Shields(params FakeShield[] shields)
    {
        private readonly FakeShield[] _all = shields;
        private readonly ConcurrentQueue<FakeShield> _left = new(shields);
        public readonly ConcurrentQueue<int> Delays = new();
        public readonly FakeTimeProvider Clock = new();

        public FakeShield Peek(int index) => _all[index];
        public readonly TaskCompletionSource Exhausted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeShield? Next()
        {
            if (_left.TryDequeue(out var shield)) return shield;
            Exhausted.TrySetResult();
            return null;
        }

        public MacShieldProcess Process() => new(new MacNetworkState(), needsWifi: false, Next)
        {
            SendReplyTimeout = TimeSpan.FromMilliseconds(200),
            Clock = Clock,
            RestartDelay = ms =>
            {
                Delays.Enqueue(ms);
                return Task.CompletedTask;
            },
        };
    }

    [Test]
    public async Task StalledSend_KillsTheShield_AndRestartsIt()
    {
        var stalled = new FakeShield(stallsWrites: true);
        var shields = new Shields(stalled, new FakeShield());
        using var process = shields.Process();

        await process.StartAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await Settles(stalled.Killed.Task), Is.True, "a broken channel kills its shield");
            Assert.That(await Until(() => process.Starts == 2), Is.True, "and the shield restarts");
        }
    }

    [Test]
    public async Task ShieldDyingBeforeAckingItsState_BacksOff()
    {
        var first = new FakeShield();
        var second = new FakeShield();
        var shields = new Shields(first, second);
        using var process = shields.Process();
        await process.StartAsync(CancellationToken.None);

        foreach (var shield in new[] { first, second })
            shield.Exit();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await Settles(shields.Exhausted.Task), Is.True);
            Assert.That(shields.Delays, Is.EqualTo(BackedOff));
        }
    }

    [Test]
    public async Task ShieldThatAckedItsState_ThenDiesAtOnce_StillBacksOff()
    {
        var shields = new Shields(new FakeShield(), new FakeShield());

        await AckThenDie(shields, upFor: TimeSpan.FromSeconds(1));

        Assert.That(shields.Delays, Is.EqualTo(BackedOff));
    }

    [Test]
    public async Task ShieldThatStayedUpAfterAcking_RestartsWithoutBackoff()
    {
        var shields = new Shields(new FakeShield(), new FakeShield());

        await AckThenDie(shields, upFor: MacShieldProcess.HealthyUptime);

        Assert.That(shields.Delays, Is.EqualTo(NotBackedOff));
    }

    // the first shield dies before acking; the restarted one acks its state, stays up for upFor, then dies
    private static async Task AckThenDie(Shields shields, TimeSpan upFor)
    {
        var first = shields.Peek(0);
        var acking = shields.Peek(1);
        using var process = shields.Process();
        _ = acking.AckStates();
        await process.StartAsync(CancellationToken.None);
        first.Exit();
        Assert.That(await Until(() => process.StatesAcked == 1), Is.True);
        shields.Clock.Advance(upFor);
        acking.Exit();
        Assert.That(await Settles(shields.Exhausted.Task), Is.True);
    }

    private static Task<bool> Until(Func<bool> condition) => Settles(Task.Run(async () =>
    {
        while (!condition()) await Task.Delay(10);
    }));
}
