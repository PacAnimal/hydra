using Hydra.Config;
using Hydra.Platform;
using Hydra.Relay;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.Platform;

[TestFixture]
public class ThreadedSleepMonitorTests
{
    [Test]
    public async Task Start_WaitsForTheLoopToBeReady_OnANamedBackgroundThread()
    {
        using var monitor = new LoopMonitor(enabled: true, available: true);

        await monitor.StartAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(monitor.LoopRan, Is.True);
            Assert.That(monitor.ThreadName, Is.EqualTo("HydraSystemSleep"));
            Assert.That(monitor.ThreadIsBackground, Is.True);
        }
    }

    [Test]
    public async Task Stop_WakesTheLoopAndJoinsIt()
    {
        using var monitor = new LoopMonitor(enabled: true, available: true);
        await monitor.StartAsync(CancellationToken.None);

        await monitor.StopAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(monitor.LoopExited, Is.True, "stop must not return while the loop is still running");
            Assert.That(monitor.SawStopping, Is.True, "the loop must see the stop flag once woken");
        }
    }

    [Test]
    public async Task UnavailableNotifications_StartAndStopCleanly()
    {
        using var monitor = new LoopMonitor(enabled: true, available: false);

        await monitor.StartAsync(CancellationToken.None);
        await monitor.StopAsync(CancellationToken.None);

        Assert.That(monitor.LoopExited, Is.True);
    }

    [Test]
    public async Task DisabledProfile_StartsNoLoop()
    {
        using var monitor = new LoopMonitor(enabled: false, available: true);

        await monitor.StartAsync(CancellationToken.None);
        await monitor.StopAsync(CancellationToken.None);

        Assert.That(monitor.LoopRan, Is.False);
    }

    private sealed class LoopMonitor(bool enabled, bool available) : ThreadedSleepMonitor(
        new SystemSleepCoordinator(
            TransitionTestHelper.Profile("host", new HydraConfig { Mode = Mode.Master, AllowSystemSleep = enabled }),
            new NullRelaySender(),
            NullLogger<SystemSleepCoordinator>.Instance),
        NullLogger.Instance)
    {
        private readonly TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool LoopRan { get; private set; }
        internal bool LoopExited { get; private set; }
        internal bool SawStopping { get; private set; }
        internal string? ThreadName { get; private set; }
        internal bool ThreadIsBackground { get; private set; }

        protected override string NotificationSource => "test sleep notifications";
        protected override string MonitorName => "test sleep monitor";

        protected override void RunLoop(TaskCompletionSource<bool> ready)
        {
            try
            {
                LoopRan = true;
                ThreadName = Thread.CurrentThread.Name;
                ThreadIsBackground = Thread.CurrentThread.IsBackground;
                ready.TrySetResult(available);
                if (!available) return;
                _wake.Task.Wait();
                SawStopping = Stopping;
            }
            finally
            {
                LoopExited = true;
            }
        }

        protected override void SignalStop() => _wake.TrySetResult();
    }
}
