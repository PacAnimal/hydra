using System.Globalization;
using System.Security.Cryptography;
using Cathedral.Styx;

namespace Styx;

/// <summary>A relay configuration it refuses to start with.</summary>
public sealed class StyxConfigurationException(string message) : Exception(message);

/// <summary>Reads a relay's settings from the environment, refusing anything a relay must not run with.</summary>
public static class StyxEnvironment
{
    // SHA-256 of the keys Styx.md's test vectors give, the key and the passphrase's, which every reader of the document holds
    private static readonly byte[][] PublishedTestKeyHashes =
    [
        Convert.FromHexString("fdeab9acf3710362bd2658cdc9a29e8f9c757fcf9811603a8c447cd1d9151108"),
        Convert.FromHexString("12e983a4d5cf95d0d28e43ded87a0097a29e6a724b61cbc80bf733625533e011"),
    ];

    public static StyxAuth Read(Func<string, string?> environment)
    {
        var keys = ReadKeys(environment);
        var password = NullIfEmpty(environment(Constants.RelayPasswordEnvVar));
        if (keys.Count == 0 && password is null)
            throw new StyxConfigurationException(
                $"None of {Constants.RelaySigningKeyEnvVar}, {Constants.RelaySigningPassphraseEnvVar} or {Constants.RelayPasswordEnvVar} is set, so no token could ever be verified");

        return new StyxAuth(
            keys,
            password,
            ReadNetworks(environment(Constants.RevokedNetworksEnvVar)),
            ReadList(environment(Constants.RevokedIdsEnvVar)).ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>
    /// The relay's v2 keys: the passphrase's first when one is set, then <c>RELAY_SIGNING_KEY</c>'s, one base64 key or during a
    /// rotation a comma-separated list. The first key mints; the rest only verify.
    /// </summary>
    public static IReadOnlyList<byte[]> ReadKeys(Func<string, string?> environment)
    {
        var keys = new List<byte[]>();
        var sources = new List<string>();
        if (NullIfEmpty(environment(Constants.RelaySigningPassphraseEnvVar)) is { } passphrase)
        {
            keys.Add(KeyFromPassphrase(passphrase));
            sources.Add(Constants.RelaySigningPassphraseEnvVar);
        }
        foreach (var entry in ReadList(environment(Constants.RelaySigningKeyEnvVar)))
        {
            byte[] key;
            try { key = Convert.FromBase64String(entry); }
            catch (FormatException) { throw new StyxConfigurationException($"A {Constants.RelaySigningKeyEnvVar} entry is not base64"); }
            if (key.Length != StyxToken.KeyBytes)
                throw new StyxConfigurationException($"A {Constants.RelaySigningKeyEnvVar} entry is {key.Length} bytes, not {StyxToken.KeyBytes}");
            keys.Add(key);
            sources.Add(Constants.RelaySigningKeyEnvVar);
        }

        for (var i = 0; i < keys.Count; i++)
        {
            var hash = SHA256.HashData(keys[i]);
            if (PublishedTestKeyHashes.Any(published => CryptographicOperations.FixedTimeEquals(hash, published)))
                throw new StyxConfigurationException($"{sources[i]} holds the TEST ONLY key or passphrase published in Styx.md");
            if (keys.Take(i).Any(k => k.AsSpan().SequenceEqual(keys[i])))
                throw new StyxConfigurationException($"{sources[i]} gives the relay a key it already has");
            if (keys.Take(i).Any(k => StyxToken.KeyIdOf(k) == StyxToken.KeyIdOf(keys[i])))
                throw new StyxConfigurationException($"{sources[i]} gives a key whose key id another key already has; generate another key or choose another passphrase");
        }
        return keys;
    }

    /// <summary>How many of a client's invocations <c>/relay/parallel</c> runs at once: <c>RELAY_PARALLEL_INVOCATIONS</c>, 4 when unset.</summary>
    public static int ReadParallelInvocations(Func<string, string?> environment)
    {
        if (NullIfEmpty(environment(Constants.ParallelInvocationsEnvVar)) is not { } value) return Constants.DefaultParallelInvocations;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var width) || width is < 1 or > Constants.ParallelInvocationsLimit)
            throw new StyxConfigurationException(
                $"{Constants.ParallelInvocationsEnvVar} is \"{value}\", not a whole number from 1 to {Constants.ParallelInvocationsLimit}");
        return width;
    }

    /// <summary>What a relay started with this parallel width should warn of, if anything.</summary>
    public static string? ParallelInvocationsWarning(int width) => width switch
    {
        1 => $"{Constants.ParallelInvocationsEnvVar} is 1, so {Constants.ParallelRelayPath} is not parallel: it runs a client's invocations one at a time, " +
             $"as {Constants.RelayPath} does",
        > Constants.ParallelInvocationsWarnAbove => $"{Constants.ParallelInvocationsEnvVar} is {width}, so one client on {Constants.ParallelRelayPath} " +
             $"can hold up to {width * Constants.MaxMessageMebiBytes} MiB of messages in flight",
        _ => null,
    };

    /// <summary>The key a passphrase derives (Styx.md §13.1).</summary>
    public static byte[] KeyFromPassphrase(string passphrase)
    {
        try { return StyxToken.KeyFromPassphrase(passphrase); }
        catch (StyxTokenException ex) { throw new StyxConfigurationException($"{Constants.RelaySigningPassphraseEnvVar}: {ex.Message}"); }
    }

    private static HashSet<Guid> ReadNetworks(string? value)
    {
        var networks = new HashSet<Guid>();
        foreach (var entry in ReadList(value))
        {
            if (!Guid.TryParse(entry, out var network))
                throw new StyxConfigurationException($"{Constants.RevokedNetworksEnvVar} holds \"{entry}\", which is not a UUID");
            networks.Add(network);
        }
        return networks;
    }

    private static string[] ReadList(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
