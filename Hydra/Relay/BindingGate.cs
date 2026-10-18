using Common;
using Styx;
using Styx.Services;
using StyxConstants = Styx.Constants;

namespace Hydra.Relay;

/// <summary>
/// Decides who may have a binding proof compared, and how fast. Every proof is compared inside a lane, one at a time,
/// and a wrong one holds its lane a throttle's length even if the guesser hangs up, so nobody can fire guesses in
/// parallel and keep only the answer that came back right. Strangers share one lane, so guessing runs at one a throttle
/// however many addresses it comes from; an address that has proven itself gets a lane of its own, so a guesser can
/// delay a first join but never a peer already in. Blocked addresses are refused before any lane, so they never hold
/// one, and a lane past <see cref="MaxWaiting"/> turns newcomers away.
///
/// <para>Accepted costs of the bound: known peers live here, so a relay restart forgets them; an attacker with many
/// addresses can keep the strangers' lane busy and delay first joins; and the limiter keys IPv6 by /64, so a device on a
/// peer's /64 can lock that peer out by guessing.</para>
/// </summary>
internal sealed class BindingGate(ResponseThrottle throttle, FailureLimiter limiter)
{
    public const int MaxWaiting = 16;
    private const int MaxKnownPeers = 1000;

    private readonly Lane _strangers = new();
    private readonly BoundedLru<string, Lane> _knownPeers = new(MaxKnownPeers);

    public async Task<BindingVerdict> Check(string remoteIp, Func<bool> proofMatches, CancellationToken cancel)
    {
        if (limiter.IsBlocked(remoteIp)) return await Refuse(cancel);

        var peer = FailureLimiter.KeyOf(remoteIp);
        var lane = _knownPeers.TryGet(peer, out var own) ? own : _strangers;
        if (!lane.TryQueue()) return BindingVerdict.Busy;
        try { await lane.Gate.WaitAsync(cancel); }
        finally { lane.Dequeue(); }

        bool blocked;
        try
        {
            // decided and counted inside the lane, so an address's guesses cannot all pass the check before one is counted
            blocked = limiter.IsBlocked(remoteIp);
            if (!blocked)
            {
                if (proofMatches())
                {
                    _knownPeers.AddOrUpdate(peer, existing => existing ?? new Lane());
                    return BindingVerdict.Proven;
                }
                limiter.RecordFailure(remoteIp);
                await throttle.Start(StyxConstants.AuthThrottleSeconds, CancellationToken.None);
            }
        }
        finally { lane.Gate.Release(); }

        return blocked ? await Refuse(cancel) : BindingVerdict.Refused;
    }

    private async Task<BindingVerdict> Refuse(CancellationToken cancel)
    {
        await throttle.Start(StyxConstants.AuthThrottleSeconds, cancel);
        return BindingVerdict.Refused;
    }

    private sealed class Lane
    {
        private int _waiting;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public bool TryQueue()
        {
            if (Interlocked.Increment(ref _waiting) <= MaxWaiting) return true;
            Interlocked.Decrement(ref _waiting);
            return false;
        }

        public void Dequeue() => Interlocked.Decrement(ref _waiting);
    }
}

internal enum BindingVerdict
{
    Proven,
    Refused,
    Busy,
}
