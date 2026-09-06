using Hydra.Config;
using Hydra.Platform;
using Hydra.Relay;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.Platform;

[TestFixture]
public class SystemSleepCoordinatorTests
{
    [Test]
    public async Task DisabledProfile_DoesNotSuspendOrResumeRelay()
    {
        var relay = new SleepRelay();
        var coordinator = Make(enabled: false, relay);

        await coordinator.PrepareForSleepAsync(CancellationToken.None);
        coordinator.BeginResumeAfterSleep();
        coordinator.ResumeAfterSleep();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(relay.SuspendCount, Is.Zero);
            Assert.That(relay.BeginWakeCount, Is.Zero);
            Assert.That(relay.CompleteWakeCount, Is.Zero);
        }
    }

    [Test]
    public async Task EnabledProfile_SuspendsOnceAndResumesOncePerSleepCycle()
    {
        var relay = new SleepRelay();
        var coordinator = Make(enabled: true, relay);

        await coordinator.PrepareForSleepAsync(CancellationToken.None);
        await coordinator.PrepareForSleepAsync(CancellationToken.None);
        coordinator.BeginResumeAfterSleep();
        coordinator.BeginResumeAfterSleep();
        coordinator.ResumeAfterSleep();
        coordinator.ResumeAfterSleep();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(relay.SuspendCount, Is.EqualTo(1));
            Assert.That(relay.BeginWakeCount, Is.EqualTo(1));
            Assert.That(relay.CompleteWakeCount, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task WakeDuringRelayShutdown_IsReappliedAfterSuspensionCompletes()
    {
        var relay = new SleepRelay { BlockSuspension = true };
        var coordinator = Make(enabled: true, relay);

        var prepare = coordinator.PrepareForSleepAsync(CancellationToken.None);
        await relay.SuspensionStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        coordinator.ResumeAfterSleep();
        relay.AllowSuspensionToComplete.TrySetResult();
        await prepare;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(relay.SuspendCount, Is.EqualTo(1));
            Assert.That(relay.BeginWakeCount, Is.Zero);
            Assert.That(relay.CompleteWakeCount, Is.EqualTo(2),
                "completed wake should be repeated after the racing suspension finishes");
        }
    }

    [Test]
    public async Task EarlyWakeDuringRelayShutdown_IsReappliedAfterSuspensionCompletes()
    {
        var relay = new SleepRelay { BlockSuspension = true };
        var coordinator = Make(enabled: true, relay);

        var prepare = coordinator.PrepareForSleepAsync(CancellationToken.None);
        await relay.SuspensionStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        coordinator.BeginResumeAfterSleep();
        relay.AllowSuspensionToComplete.TrySetResult();
        await prepare;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(relay.SuspendCount, Is.EqualTo(1));
            Assert.That(relay.BeginWakeCount, Is.EqualTo(2),
                "early wake should be repeated after the racing suspension finishes");
            Assert.That(relay.CompleteWakeCount, Is.Zero);
        }
    }

    [Test]
    public async Task CompletedWakeWithoutEarlyNotification_ResumesRelay()
    {
        var relay = new SleepRelay();
        var coordinator = Make(enabled: true, relay);

        await coordinator.PrepareForSleepAsync(CancellationToken.None);
        coordinator.ResumeAfterSleep();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(relay.BeginWakeCount, Is.Zero);
            Assert.That(relay.CompleteWakeCount, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task ConsecutiveSleepCycles_UseDistinctOrderedGenerations()
    {
        var relay = new SleepRelay();
        var coordinator = Make(enabled: true, relay);

        await coordinator.PrepareForSleepAsync(CancellationToken.None);
        coordinator.BeginResumeAfterSleep();
        coordinator.ResumeAfterSleep();
        await coordinator.PrepareForSleepAsync(CancellationToken.None);
        coordinator.BeginResumeAfterSleep();
        coordinator.ResumeAfterSleep();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(relay.SleepGenerations, Is.EqualTo(new long[] { 1, 2 }));
            Assert.That(relay.WakeGenerations, Is.EqualTo(new long[] { 1, 1, 2, 2 }));
        }
    }

    private static SystemSleepCoordinator Make(bool enabled, SleepRelay relay) => new(
        TransitionTestHelper.Profile("host", new HydraConfig
        {
            Mode = Mode.Master,
            AllowSystemSleep = enabled
        }), relay, NullLogger<SystemSleepCoordinator>.Instance);

    private sealed class SleepRelay : IRelaySender
    {
        internal bool BlockSuspension { get; init; }
        internal TaskCompletionSource SuspensionStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource AllowSuspensionToComplete { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int SuspendCount { get; private set; }
        internal List<long> SleepGenerations { get; } = [];
        internal int BeginWakeCount { get; private set; }
        internal int CompleteWakeCount { get; private set; }
        internal List<long> WakeGenerations { get; } = [];
        public bool IsConnected => true;
        public void Send(string[] targetHosts, byte[] payload) { }
        public async ValueTask SuspendConnectionAsync(CancellationToken cancel = default)
        {
            SuspendCount++;
            SuspensionStarted.TrySetResult();
            if (BlockSuspension)
                await AllowSuspensionToComplete.Task.WaitAsync(cancel);
        }
        public ValueTask SuspendForSystemSleepAsync(long generation, CancellationToken cancel = default)
        {
            SleepGenerations.Add(generation);
            return SuspendConnectionAsync(cancel);
        }
        public void BeginSystemWake(long generation)
        {
            BeginWakeCount++;
            WakeGenerations.Add(generation);
        }
        public void CompleteSystemWake(long generation)
        {
            CompleteWakeCount++;
            WakeGenerations.Add(generation);
        }
#pragma warning disable CS0067
        public event Func<string[], Task>? PeersChanged;
        public event Func<string, MessageKind, ReadOnlyMemory<byte>, Task>? MessageReceived;
        public event Func<Task>? Disconnected;
#pragma warning restore CS0067
    }
}
