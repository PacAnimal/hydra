using System.Security.Cryptography;
using Cathedral.Styx;

namespace Styx;

/// <summary>A relay configuration it refuses to start with.</summary>
public sealed class StyxConfigurationException(string message) : Exception(message);

/// <summary>Reads <see cref="StyxAuth"/> from the environment, refusing anything a relay must not run with.</summary>
public static class StyxEnvironment
{
    // SHA-256 of the key Styx.md's test vectors are signed with, which every reader of the document holds
    private static readonly byte[] PublishedTestKeyHash = Convert.FromHexString("630dcd2966c4336691125448bbb25b4ff412a49c732db2c8abc1b8581bd710dd");

    public static StyxAuth Read(Func<string, string?> environment)
    {
        var keys = ReadKeys(environment(Constants.RelayKeyEnvVar));
        var password = NullIfEmpty(environment(Constants.RelayPasswordEnvVar));
        if (keys.Count == 0 && password is null)
            throw new StyxConfigurationException($"Neither {Constants.RelayKeyEnvVar} nor {Constants.RelayPasswordEnvVar} is set, so no token could ever be verified");

        return new StyxAuth(
            keys,
            password,
            NullIfEmpty(environment(Constants.RelayAdminPasswordEnvVar)) ?? password,
            ReadNetworks(environment(Constants.RevokedNetworksEnvVar)),
            ReadList(environment(Constants.RevokedIdsEnvVar)).ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>One base64 key, or during a rotation a comma-separated list whose first entry mints.</summary>
    public static IReadOnlyList<byte[]> ReadKeys(string? value)
    {
        var keys = new List<byte[]>();
        foreach (var entry in ReadList(value))
        {
            byte[] key;
            try { key = Convert.FromBase64String(entry); }
            catch (FormatException) { throw new StyxConfigurationException($"A {Constants.RelayKeyEnvVar} entry is not base64"); }
            if (key.Length != StyxToken.KeyBytes)
                throw new StyxConfigurationException($"A {Constants.RelayKeyEnvVar} entry is {key.Length} bytes, not {StyxToken.KeyBytes}");
            if (CryptographicOperations.FixedTimeEquals(SHA256.HashData(key), PublishedTestKeyHash))
                throw new StyxConfigurationException($"{Constants.RelayKeyEnvVar} holds the TEST ONLY key published in Styx.md");
            if (keys.Any(k => k.AsSpan().SequenceEqual(key)))
                throw new StyxConfigurationException($"{Constants.RelayKeyEnvVar} lists the same key twice");
            if (keys.Any(k => StyxToken.KeyIdOf(k) == StyxToken.KeyIdOf(key)))
                throw new StyxConfigurationException($"Two {Constants.RelayKeyEnvVar} entries share a key id; generate another key");
            keys.Add(key);
        }
        return keys;
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
