using Hydra.Relay;
using Styx;
using Styx.Services;
using Tests.Setup;
using StyxConstants = Styx.Constants;

namespace Tests.Relay;

// the gate's waits are registered before Check returns, so each assertion here reads settled state, never a race
[TestFixture]
public class BindingGateTests
{
    private const string Peer = "192.168.1.10";
    private const string Stranger = "192.168.1.66";
    private const string OtherStranger = "192.168.1.77";
    private static readonly TimeSpan Throttle = TimeSpan.FromSeconds(StyxConstants.AuthThrottleSeconds);

    private StartedTimers _clock = null!;
    private FailureLimiter _limiter = null!;
    private BindingGate _gate = null!;

    [SetUp]
    public void SetUp()
    {
        _clock = new StartedTimers();
        _limiter = new FailureLimiter(_clock);
        _gate = new BindingGate(new ResponseThrottle(_clock), _limiter);
    }

    private Task<BindingVerdict> Check(string remoteIp, Func<bool> proofMatches) => _gate.Check(remoteIp, proofMatches, CancellationToken.None);

    private Task<TimeSpan> ThrottleStarted() => _clock.Started.Next("a throttle");

    private async Task Join(string remoteIp) => Assert.That(await Check(remoteIp, () => true).WaitAsync(Bound), Is.EqualTo(BindingVerdict.Proven));

    // the point of the gate: no address can have proofs compared in parallel, right ones included
    [Test]
    public async Task AStrangersProofIsNotComparedWhileAnotherStrangersWrongOneHoldsTheLane()
    {
        var compared = 0;
        var wrong = Check(Stranger, () => ++compared < 0);
        await ThrottleStarted();

        var right = Check(OtherStranger, () => ++compared > 0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(compared, Is.EqualTo(1), "a second proof was compared while the first one's throttle held the lane");
            Assert.That(right.IsCompleted, Is.False);
        }

        _clock.Advance(Throttle);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await wrong.WaitAsync(Bound), Is.EqualTo(BindingVerdict.Refused));
            Assert.That(await right.WaitAsync(Bound), Is.EqualTo(BindingVerdict.Proven));
        }
    }

    // a known peer's address gets its own lane, which still compares one at a time: sharing a peer's address buys no speed
    [Test]
    public async Task AKnownPeersProofsAreAlsoComparedOneAtATime()
    {
        await Join(Peer);
        var compared = 0;
        var wrong = Check(Peer, () => ++compared < 0);
        await ThrottleStarted();

        var next = Check(Peer, () => ++compared > 0);
        Assert.That(compared, Is.EqualTo(1));

        _clock.Advance(Throttle);
        await wrong.WaitAsync(Bound);
        Assert.That(await next.WaitAsync(Bound), Is.EqualTo(BindingVerdict.Proven));
    }

    [Test]
    public async Task AKnownPeerIsNeverQueuedBehindAStranger()
    {
        await Join(Peer);
        var wrong = Check(Stranger, () => false);
        await ThrottleStarted();

        var known = Check(Peer, () => true);

        Assert.That(known.IsCompletedSuccessfully && known.Result == BindingVerdict.Proven, Is.True);
        _clock.Advance(Throttle);
        await wrong.WaitAsync(Bound);
    }

    // walking away from a guess must not free the lane early, or abandoning guesses would be the way to guess fast
    [Test]
    public async Task AbandoningAGuessStillHoldsTheLaneForTheThrottle()
    {
        using var abandon = new CancellationTokenSource();
        var wrong = _gate.Check(Stranger, () => false, abandon.Token);
        await ThrottleStarted();

        await abandon.CancelAsync();
        var next = Check(OtherStranger, () => true);

        Assert.That(next.IsCompleted, Is.False, "cancelling the guess released the lane before its throttle ran out");
        _clock.Advance(Throttle);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await wrong.WaitAsync(Bound), Is.EqualTo(BindingVerdict.Refused));
            Assert.That(await next.WaitAsync(Bound), Is.EqualTo(BindingVerdict.Proven));
        }
    }

    // a wrong proof is counted the moment it is compared, so a burst cannot all pass the block check first
    [Test]
    public async Task AFailureIsCountedBeforeItsThrottle()
    {
        await Join(Peer);
        for (var i = 1; i < FailureLimiter.MaxFailures; i++) _limiter.RecordFailure(Peer);
        var compared = 0;
        var last = Check(Peer, () => ++compared < 0);
        await ThrottleStarted();

        var after = Check(Peer, () => ++compared > 0);
        await ThrottleStarted();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(compared, Is.EqualTo(1), "the address was compared again after its limit was reached");
            Assert.That(_limiter.IsBlocked(Peer), Is.True);
        }
        _clock.Advance(Throttle);
        Assert.That(await Task.WhenAll(last, after).WaitAsync(Bound), Is.All.EqualTo(BindingVerdict.Refused));
    }

    // a blocked address is refused before any lane, so it cannot keep strangers from joining
    [Test]
    public async Task ABlockedAddressNeverHoldsTheStrangersLane()
    {
        for (var i = 0; i < FailureLimiter.MaxFailures; i++) _limiter.RecordFailure(Stranger);
        var compared = false;
        var blocked = Check(Stranger, () => compared = true);
        await ThrottleStarted();

        var joining = Check(OtherStranger, () => true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(compared, Is.False);
            Assert.That(joining.IsCompletedSuccessfully && joining.Result == BindingVerdict.Proven, Is.True);
        }
        _clock.Advance(Throttle);
        Assert.That(await blocked.WaitAsync(Bound), Is.EqualTo(BindingVerdict.Refused));
    }

    // past the cap a newcomer is turned away at once rather than holding a connection in an ever longer queue
    [Test]
    public async Task NewcomersPastTheQueueCapAreTurnedAway()
    {
        var holding = Check(Stranger, () => false);
        await ThrottleStarted();
        var queued = Enumerable.Range(0, BindingGate.MaxWaiting).Select(_ => Check(OtherStranger, () => true)).ToArray();

        var turnedAway = Check(OtherStranger, () => true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(queued.Any(q => q.IsCompleted), Is.False);
            Assert.That(turnedAway.IsCompletedSuccessfully && turnedAway.Result == BindingVerdict.Busy, Is.True);
        }
        _clock.Advance(Throttle);
        await holding.WaitAsync(Bound);
        Assert.That(await Task.WhenAll(queued).WaitAsync(Bound), Is.All.EqualTo(BindingVerdict.Proven));
    }
}
