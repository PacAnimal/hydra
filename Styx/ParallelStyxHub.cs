using Styx.Services;

namespace Styx;

/// <summary>
/// The relay at <see cref="Constants.ParallelRelayPath"/>: the same hub, running up to <see cref="StyxOptions.ParallelInvocations"/>
/// of a client's invocations at once, so with no order between them. SignalR keys options by hub type, hence a type of its own.
/// </summary>
public sealed class ParallelStyxHub(IClientRegistry registry, IPeerBroadcaster peers, ITokenVerifier verifier, IExpiryScheduler expiry,
    PeerWriter writer, ILogger<StyxHub> log, StyxOptions options, ResponseThrottle throttler)
    : StyxHub(registry, peers, verifier, expiry, writer, log, options, throttler);
