using Cathedral.Extensions;
using Cathedral.Styx;

namespace Styx.Services;

public interface ITokenVerifier
{
    Task<TokenVerdict> Verify(string authorization, string remoteIp, CancellationToken cancel);
}

/// <summary>
/// The one check behind both <c>Authenticate</c> and <c>/api/status</c>, so the two cannot drift. A token starting
/// <c>styx2.</c> is v2, anything else v1. A network is one or the other: v2 tokens name version-8 networks only, v1
/// tokens never do.
/// </summary>
public sealed class TokenVerifier(StyxAuth auth, V1TokenVerifier v1, TimeProvider clock) : ITokenVerifier
{
    public const string InvalidAuthorization = "Invalid authorization";
    public const string AuthorizationExpired = "Authorization expired";

    public async Task<TokenVerdict> Verify(string authorization, string remoteIp, CancellationToken cancel)
    {
        if (authorization.StartsWithOrdinal(StyxToken.Prefix)) return VerifyV2(authorization);

        var network = await v1.Verify(authorization, remoteIp, cancel);
        if (network is not { } id || auth.RevokedNetworks.Contains(id)) return TokenVerdict.Refused(InvalidAuthorization);
        return TokenVerdict.Accepted(id, null);
    }

    private TokenVerdict VerifyV2(string authorization)
    {
        if (!StyxToken.TryParse(authorization, out var token)) return TokenVerdict.Refused(InvalidAuthorization);
        var key = auth.KeyFor(token.KeyId);
        if (key is null || !token.Verify(key)) return TokenVerdict.Refused(InvalidAuthorization);

        var rules = token.Describe();
        if (auth.RevokedNetworks.Contains(token.Network) || rules.Ids.Any(auth.RevokedIds.Contains))
            return TokenVerdict.Refused(InvalidAuthorization);
        if (rules.IsExpiredAt(clock.GetUtcNow())) return TokenVerdict.Refused(AuthorizationExpired);
        return TokenVerdict.Accepted(new NetworkKey(token.Network, rules.PathKey), rules);
    }
}

/// <summary>Either the network a token admits to, with a v2 token's rules, or why it was refused.</summary>
public sealed record TokenVerdict(NetworkKey? Network, StyxTokenRules? Rules, string? Failure)
{
    public static TokenVerdict Accepted(NetworkKey network, StyxTokenRules? rules) => new(network, rules, null);

    public static TokenVerdict Refused(string failure) => new(null, null, failure);
}
