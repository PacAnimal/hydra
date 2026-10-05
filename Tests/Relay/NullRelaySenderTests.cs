using Hydra.Relay;
using Tests.Setup;

namespace Tests.Relay;

[TestFixture]
public class NullRelaySenderTests
{
    [Test]
    public async Task AnOverride_IsWhatTheInterfaceCalls()
    {
        IRelaySender relay = new OverridingRelay();

        await relay.SendReliableAsync(["peer"], TestMessages.Move(1));
        await relay.SuspendConnectionAsync();
        relay.ResumeConnection();

        Assert.That(((OverridingRelay)relay).Calls, Is.EqualTo(["reliable", "suspend", "resume"]));
    }

    [Test]
    public async Task TheDefaults_RouteThroughTheMembersTheyAlwaysDid()
    {
        var recording = new OverridingRelay();
        IRelaySender relay = recording;

        await relay.SuspendForSystemSleepAsync(1);
        relay.BeginSystemWake(1);
        relay.CompleteSystemWake(1);

        Assert.That(recording.Calls, Is.EqualTo(["suspend", "resume", "resume"]));
    }

    [Test]
    public async Task SendReliable_SendsOnce_AndHonoursCancellation()
    {
        var relay = new CountingRelay();
        var cancelled = new CancellationToken(canceled: true);

        await relay.SendReliableAsync(["peer"], TestMessages.Move(1));
        relay.SendMouseDelta(["peer"], 1, 2);
        relay.SendKeyEvent(["peer"], TestMessages.KeyMessage(0));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(relay.Sends, Is.EqualTo(3));
            Assert.That(async () => await relay.SendReliableAsync(["peer"], TestMessages.Move(1), cancelled), Throws.InstanceOf<OperationCanceledException>());
            Assert.That(relay.RequestReconnect(), Is.False);
            Assert.That(relay.Transport, Is.Null);
        }
    }

    private sealed class OverridingRelay : NullRelaySender
    {
        public List<string> Calls { get; } = [];

        public override ValueTask SendReliableAsync(string[] targetHosts, byte[] payload, CancellationToken cancel = default)
        {
            Calls.Add("reliable");
            return ValueTask.CompletedTask;
        }

        public override ValueTask SuspendConnectionAsync(CancellationToken cancel = default)
        {
            Calls.Add("suspend");
            return ValueTask.CompletedTask;
        }

        public override void ResumeConnection() => Calls.Add("resume");
    }

    private sealed class CountingRelay : NullRelaySender
    {
        public int Sends { get; private set; }
        public override void Send(string[] targetHosts, byte[] payload) => Sends++;
    }
}
