using Hydra.Config;
using Hydra.Relay;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.Relay;

[TestFixture]
public class RelayWakeRetryTests
{
    [Test]
    public void WakePhases_UseFastRetryUntilCompletionGraceExpires()
    {
        var relay = new WakeDelayRelay(TimeSpan.FromHours(1));

        Assert.That(relay.Delay, Is.EqualTo(TimeSpan.FromSeconds(15)));

        relay.BeginSystemWake(1);
        Assert.That(relay.Delay, Is.EqualTo(TimeSpan.FromSeconds(1)));

        relay.CompleteSystemWake(1);
        Assert.That(relay.Delay, Is.EqualTo(TimeSpan.FromSeconds(1)));
    }

    [Test]
    public void CompletedWake_WithNoGrace_UsesNormalRetry()
    {
        var relay = new WakeDelayRelay(TimeSpan.Zero);

        relay.BeginSystemWake(1);
        relay.CompleteSystemWake(1);

        Assert.That(relay.Delay, Is.EqualTo(TimeSpan.FromSeconds(15)));
    }

    [Test]
    public void EarlyWakeWithoutCompletion_IsBounded()
    {
        var relay = new WakeDelayRelay(TimeSpan.Zero, TimeSpan.Zero);

        relay.BeginSystemWake(1);

        Assert.That(relay.Delay, Is.EqualTo(TimeSpan.FromSeconds(15)));
    }

    [Test]
    public async Task ExpiredWakeWindow_ReturnsToNormalRetryAfterEarlierFastObservation()
    {
        var relay = new WakeDelayRelay(TimeSpan.Zero, TimeSpan.FromMilliseconds(20));

        relay.BeginSystemWake(1);
        Assert.That(relay.Delay, Is.EqualTo(TimeSpan.FromSeconds(1)));

        await Task.Delay(TimeSpan.FromMilliseconds(100));

        Assert.That(relay.Delay, Is.EqualTo(TimeSpan.FromSeconds(15)));
    }

    [Test]
    public async Task EarlyWake_InterruptsExistingNormalReconnectDelay()
    {
        var relay = new WakeDelayRelay(TimeSpan.FromHours(1));
        var delay = relay.WaitForDelay(TimeSpan.FromHours(1));

        relay.BeginSystemWake(1);

        await delay.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Test]
    public void LateEarlyWake_DoesNotOverrideCompletedWake()
    {
        var relay = new WakeDelayRelay(TimeSpan.Zero, TimeSpan.FromHours(1));

        relay.CompleteSystemWake(1);
        relay.BeginSystemWake(1);

        Assert.That(relay.Delay, Is.EqualTo(TimeSpan.FromSeconds(15)));
    }

    [Test]
    public async Task StaleWake_DoesNotReleaseOrAccelerateNewerSleepCycle()
    {
        var relay = new WakeDelayRelay(TimeSpan.FromHours(1));

        await relay.SuspendForSystemSleepAsync(2);
        relay.CompleteSystemWake(1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(relay.Delay, Is.EqualTo(TimeSpan.FromSeconds(15)));
            Assert.That(relay.Suspended, Is.True);
        }
    }

    private sealed class WakeDelayRelay(
        TimeSpan grace,
        TimeSpan? earlyWindow = null) : RelayConnection(
        TransitionTestHelper.Profile("wake-test", new HydraConfig
        {
            Mode = Mode.Master,
            NetworkConfig = "unused"
        }),
        NullLogger<RelayConnection>.Instance,
        new WorldState())
    {
        protected override TimeSpan EarlySystemWakeReconnectWindow =>
            earlyWindow ?? TimeSpan.FromHours(1);
        protected override TimeSpan SystemWakeReconnectGracePeriod => grace;
        internal TimeSpan Delay => CurrentReconnectDelay();
        internal bool Suspended => ConnectionSuspended;
        internal Task WaitForDelay(TimeSpan delay) =>
            DelayBeforeReconnect(delay, WakeStateVersion, CancellationToken.None);
    }
}
