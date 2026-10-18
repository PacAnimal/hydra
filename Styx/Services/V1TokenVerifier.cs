using Common;
using System.Security.Cryptography;
using System.Text.Json;
using Cathedral.Utils;

namespace Styx.Services;

/// <summary>
/// v1 tokens: AES under a key derived from <c>RELAY_PASSWORD</c>, over a JSON-quoted network UUID, which costs a 100 000-iteration PBKDF2
/// to check. Everything here is about not paying that for nothing: junk is refused by shape, a token already proven is
/// answered from a cache, failures count toward the address limit, and only a few derivations run at once.
/// </summary>
public sealed class V1TokenVerifier(StyxAuth auth, FailureLimiter limiter, TimeProvider clock, ILogger<V1TokenVerifier> log)
{
    // every v1 token: 92 bytes of salt, nonce and tag, then the 38-byte JSON string of a UUID
    public const int TokenBytes = 130;
    public const int CacheCapacity = 10_000;
    public static readonly TimeSpan DerivationWait = TimeSpan.FromSeconds(3);

    private readonly SimpleAes? _aes = auth.AcceptsV1 ? new SimpleAes(auth.V1Password!) : null;
    private readonly SemaphoreSlim _derivations = new(Math.Max(2, Environment.ProcessorCount));
    private readonly BoundedLru<string, Guid> _proven = new(CacheCapacity);
    private readonly BoundedLru<string, DateOnly> _loggedOn = new(CacheCapacity);
    private long _derivationCount;

    /// <summary>The derivation slots, so a test can take them all.</summary>
    internal SemaphoreSlim Derivations => _derivations;

    /// <summary>How many PBKDF2 derivations have run, so a test can show junk never costs one.</summary>
    internal long DerivationCount => Interlocked.Read(ref _derivationCount);

    /// <summary>The network a valid v1 token names, or null. Never a version-8 network: those are v2 only.</summary>
    public async Task<Guid?> Verify(string authorization, string remoteIp, CancellationToken cancel)
    {
        if (_aes is null || !TryDecode(authorization, out var encrypted)) return null;

        var fingerprint = Convert.ToHexString(SHA256.HashData(encrypted));
        if (!_proven.TryGet(fingerprint, out var network))
        {
            if (limiter.IsBlocked(remoteIp)) return null;
            if (!await TakeDerivation(cancel)) return null;
            try
            {
                // many connections from one address can all pass the check above and queue here together
                if (limiter.IsBlocked(remoteIp)) return null;
                Interlocked.Increment(ref _derivationCount);
                var decrypted = await _aes.Decrypt(encrypted, false, cancel);
                network = JsonSerializer.Deserialize<Guid>(decrypted);
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException or ArgumentException)
            {
                limiter.RecordFailure(remoteIp);
                return null;
            }
            finally
            {
                _derivations.Release();
            }
            _proven.Set(fingerprint, network);
        }

        if (network.Version == 8) return null;
        LogOncePerDay(fingerprint, network, remoteIp);
        return network;
    }

    // false once DerivationWait has passed on the relay's clock
    private async Task<bool> TakeDerivation(CancellationToken cancel)
    {
        using var timeout = new CancellationTokenSource(DerivationWait, clock);
        using var either = CancellationTokenSource.CreateLinkedTokenSource(cancel, timeout.Token);
        try
        {
            await _derivations.WaitAsync(either.Token);
            return true;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancel.IsCancellationRequested)
        {
            return false;
        }
    }

    private void LogOncePerDay(string fingerprint, Guid network, string remoteIp)
    {
        if (!_loggedOn.SetIfChanged(fingerprint, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime))) return;
        if (log.IsEnabled(LogLevel.Information))
            log.LogInformation("v1 token in use on network {NetworkId} from {RemoteIp}", network, remoteIp);
    }

    // canonical standard base64 of exactly the v1 size, so nothing else ever reaches PBKDF2
    private static bool TryDecode(string authorization, out byte[] encrypted)
    {
        encrypted = new byte[TokenBytes];
        return Convert.TryFromBase64String(authorization, encrypted, out var written) && written == TokenBytes
            && Convert.ToBase64String(encrypted).Equals(authorization, StringComparison.Ordinal);
    }
}
