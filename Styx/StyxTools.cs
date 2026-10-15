using System.Globalization;
using System.Security.Cryptography;
using Cathedral.Styx;

namespace Styx;

/// <summary>
/// The relay binary's key-holding subcommands. Run them where <c>RELAY_SIGNING_KEY</c> or <c>RELAY_SIGNING_PASSPHRASE</c> already
/// lives (inside the relay's pod), so it never lands on an operator's machine; what they print is a secret too.
/// </summary>
public static class StyxTools
{
    public const string Usage = """
        usage: Styx keygen
               Styx derive                  (reads a passphrase from standard input, prints its key)
               Styx mint [--network <version-8 uuid>] [--sub <name>]... [--host <name>] [--host-prefix <prefix>]...
                         [--host-not <name>]... [--exp <unix seconds>] [--id <id>]... [--max-length <chars>]
                         [--server <url> [--encryption-key <key>] [--max-blob <bytes>]]
        """;

    private const string EncryptionKeyAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    public static bool IsCommand(string arg) => arg is "keygen" or "derive" or "mint";

    /// <returns>0 on success, 1 when refused, 2 for a usage error</returns>
    public static int Run(string[] args, Func<string, string?> environment, TextReader input, TextWriter output, TextWriter error)
    {
        try
        {
            switch (args[0])
            {
                case "keygen" when args.Length == 1:
                    output.WriteLine(Convert.ToBase64String(StyxToken.NewKey()));
                    return 0;
                // standard input, so the passphrase stays out of shell history and process listings
                case "derive" when args.Length == 1:
                    output.WriteLine(Convert.ToBase64String(StyxEnvironment.KeyFromPassphrase(
                        input.ReadLine() ?? throw new StyxConfigurationException("No passphrase on standard input; with docker, run it with -i"))));
                    return 0;
                case "mint":
                    output.WriteLine(Mint(args[1..], environment));
                    return 0;
                default:
                    error.WriteLine(Usage);
                    return 2;
            }
        }
        catch (UsageException ex)
        {
            error.WriteLine(ex.Message);
            error.WriteLine(Usage);
            return 2;
        }
        catch (Exception ex) when (ex is StyxConfigurationException or StyxTokenException)
        {
            error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static string Mint(string[] args, Func<string, string?> environment)
    {
        var keys = StyxEnvironment.ReadKeys(environment);
        if (keys.Count == 0) throw new StyxConfigurationException($"Neither {Constants.RelaySigningKeyEnvVar} nor {Constants.RelaySigningPassphraseEnvVar} is set");

        Guid? network = null;
        var caveats = new List<StyxCaveat>();
        var maxLength = int.MaxValue;
        var maxBlob = int.MaxValue;
        string? server = null;
        string? encryptionKey = null;

        for (var i = 0; i < args.Length; i++)
        {
            var option = args[i];
            if (i + 1 == args.Length) throw new UsageException($"{option} needs a value");
            var value = args[++i];
            switch (option)
            {
                case "--network":
                    network = Guid.TryParse(value, out var id) && id.Version == 8 ? id : throw new UsageException("--network must be a version-8 UUID");
                    break;
                case "--sub": caveats.Add(StyxCaveat.Sub(value)); break;
                case "--host": caveats.Add(StyxCaveat.Host(value)); break;
                case "--host-prefix": caveats.Add(StyxCaveat.HostPrefix(value)); break;
                case "--host-not": caveats.Add(StyxCaveat.HostNot(value)); break;
                case "--exp": caveats.Add(new StyxCaveat(StyxCaveat.ExpKey, value)); break;
                case "--id": caveats.Add(StyxCaveat.Id(value)); break;
                case "--max-length": maxLength = Number(value, "--max-length"); break;
                case "--max-blob": maxBlob = Number(value, "--max-blob"); break;
                case "--server": server = value; break;
                case "--encryption-key": encryptionKey = value; break;
                default: throw new UsageException($"unknown option {option}");
            }
        }

        var token = StyxToken.Mint(keys[0], network ?? StyxToken.NewNetworkId()).Narrow(maxLength, caveats);
        if (server is null)
        {
            if (encryptionKey is not null || maxBlob != int.MaxValue) throw new UsageException("--encryption-key and --max-blob need --server");
            return token.ToString();
        }

        var blob = new StyxBlob(server, encryptionKey ?? RandomNumberGenerator.GetString(EncryptionKeyAlphabet, 128), token.ToString());
        if (blob.ByteLength > maxBlob) throw new StyxTokenException($"The blob would be {blob.ByteLength} bytes, more than {maxBlob}");
        return blob.Write();
    }

    private static int Number(string value, string option) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number
            : throw new UsageException($"{option} must be a positive number");

    private sealed class UsageException(string message) : Exception(message);
}
