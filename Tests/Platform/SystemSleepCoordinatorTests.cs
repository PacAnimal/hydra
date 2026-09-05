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
        coordinator.ResumeAfterSleep();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(relay.SuspendCount, Is.Zero);
            Assert.That(relay.ResumeCount, Is.Zero);
        }
    }

    [Test]
    public async Task EnabledProfile_SuspendsOnceAndResumesOncePerSleepCycle()
    {
        var relay = new SleepRelay();
        var coordinator = Make(enabled: true, relay);

        await coordinator.PrepareForSleepAsync(CancellationToken.None);
        await coordinator.PrepareForSleepAsync(CancellationToken.None);
        coordinator.ResumeAfterSleep();
        coordinator.ResumeAfterSleep();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(relay.SuspendCount, Is.EqualTo(1));
            Assert.That(relay.ResumeCount, Is.EqualTo(1));
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
            Assert.That(relay.ResumeCount, Is.EqualTo(2),
                "wake should be repeated after the racing suspension finishes");
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
        internal int ResumeCount { get; private set; }
        public bool IsConnected => true;
        public void Send(string[] targetHosts, byte[] payload) { }
        public async ValueTask SuspendConnectionAsync(CancellationToken cancel = default)
        {
            SuspendCount++;
            SuspensionStarted.TrySetResult();
            if (BlockSuspension)
                await AllowSuspensionToComplete.Task.WaitAsync(cancel);
        }
        public void ResumeConnection() => ResumeCount++;
#pragma warning disable CS0067
        public event Func<string[], Task>? PeersChanged;
        public event Func<string, MessageKind, ReadOnlyMemory<byte>, Task>? MessageReceived;
        public event Func<Task>? Disconnected;
#pragma warning restore CS0067
    }
}
