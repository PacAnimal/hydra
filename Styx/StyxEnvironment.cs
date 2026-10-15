using System.Security.Cryptography;
using Cathedral.Styx;

namespace Styx;

/// <summary>A relay configuration it refuses to start with.</summary>
public sealed class StyxConfigurationException(string message) : Exception(message);

/// <summary>Reads <see cref="StyxAuth"/> from the environment, refusing anything a relay must not run with.</summary>
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
                $"None of {Constants.RelayKeyEnvVar}, {Constants.RelayKeyPassphraseEnvVar} or {Constants.RelayPasswordEnvVar} is set, so no token could ever be verified");

        return new StyxAuth(
            keys,
            password,
            NullIfEmpty(environment(Constants.RelayAdminPasswordEnvVar)) ?? password,
            ReadNetworks(environment(Constants.RevokedNetworksEnvVar)),
            ReadList(environment(Constants.RevokedIdsEnvVar)).ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>
    /// The relay's v2 keys: the passphrase's first when one is set, then <c>RELAY_KEY</c>'s, one base64 key or during a
    /// rotation a comma-separated list. The first key mints; the rest only verify.
    /// </summary>
    public static IReadOnlyList<byte[]> ReadKeys(Func<string, string?> environment)
    {
        var keys = new List<byte[]>();
        var sources = new List<string>();
        if (NullIfEmpty(environment(Constants.RelayKeyPassphraseEnvVar)) is { } passphrase)
        {
            keys.Add(KeyFromPassphrase(passphrase));
            sources.Add(Constants.RelayKeyPassphraseEnvVar);
        }
        foreach (var entry in ReadList(environment(Constants.RelayKeyEnvVar)))
        {
            byte[] key;
            try { key = Convert.FromBase64String(entry); }
            catch (FormatException) { throw new StyxConfigurationException($"A {Constants.RelayKeyEnvVar} entry is not base64"); }
            if (key.Length != StyxToken.KeyBytes)
                throw new StyxConfigurationException($"A {Constants.RelayKeyEnvVar} entry is {key.Length} bytes, not {StyxToken.KeyBytes}");
            keys.Add(key);
            sources.Add(Constants.RelayKeyEnvVar);
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

    /// <summary>The key a passphrase derives (Styx.md §13.1).</summary>
    public static byte[] KeyFromPassphrase(string passphrase)
    {
        try { return StyxToken.KeyFromPassphrase(passphrase); }
        catch (StyxTokenException ex) { throw new StyxConfigurationException($"{Constants.RelayKeyPassphraseEnvVar}: {ex.Message}"); }
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
