using System.Net;
using System.Net.Sockets;

namespace Styx.Services;

/// <summary>
/// Counts failed attempts on the paths where a failure is expensive or guessable: v1 tokens that do not decrypt
/// (each one a PBKDF2) and wrong relay passwords. Past <see cref="MaxFailures"/> in a minute an address is refused on
/// those paths, without any crypto, until the minute passes. v2 logins and v1 cache hits never consult it, so a
/// misconfigured device behind a shared NAT cannot lock its neighbours out.
/// </summary>
public sealed class FailureLimiter(TimeProvider clock)
{
    public const int MaxFailures = 20;
    public const int Capacity = 10_000;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly BoundedLru<string, Tally> _tallies = new(Capacity);

    public bool IsBlocked(string remoteIp) =>
        _tallies.TryGet(KeyOf(remoteIp), out var tally) && Current(tally) is { Failures: >= MaxFailures };

    public void RecordFailure(string remoteIp) =>
        _tallies.AddOrUpdate(KeyOf(remoteIp), existing =>
            existing is not null && Current(existing) is { } tally ? tally with { Failures = tally.Failures + 1 } : new Tally(clock.GetUtcNow(), 1));

    /// <summary>IPv6 by its /64, since one host commonly holds a whole one; IPv4, mapped or not, by its address.</summary>
    internal static string KeyOf(string remoteIp)
    {
        if (!IPAddress.TryParse(remoteIp, out var address)) return remoteIp;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();
        var bytes = address.GetAddressBytes();
        return Convert.ToHexString(bytes, 0, 8) + "/64";
    }

    private Tally? Current(Tally tally) => clock.GetUtcNow() - tally.WindowStart < Window ? tally : null;

    private sealed record Tally(DateTimeOffset WindowStart, int Failures);
}
