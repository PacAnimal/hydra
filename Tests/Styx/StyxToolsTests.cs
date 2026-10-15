using Cathedral.Styx;
using Styx;
using Tests.Setup;

namespace Tests.Styx;

/// <summary>The relay binary's <c>keygen</c>, <c>derive</c> and <c>mint</c> subcommands.</summary>
[TestFixture]
public class StyxToolsTests
{
    private static readonly string Key = Convert.ToBase64String(StyxTestServer.TestKey);

    [Test]
    public void Keygen_PrintsAKeyTheRelayAccepts()
    {
        var (code, output, _) = Run(["keygen"]);
        var key = output.Trim();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(code, Is.Zero);
            Assert.That(StyxEnvironment.ReadKeys(name => name == Constants.RelayKeyEnvVar ? key : null), Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void Mint_PrintsATokenTheKeyVerifies_ForAFreshVersion8Network()
    {
        var (code, output, _) = Run(["mint", "--sub", "game", "--host-not", "admin", "--id", "page"], Key);
        var token = StyxToken.Parse(output.Trim());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(code, Is.Zero);
            Assert.That(token.Verify(StyxTestServer.TestKey), Is.True);
            Assert.That(token.Network.Version, Is.EqualTo(8));
            Assert.That(token.Caveats.Select(c => c.ToString()), Is.EqualTo(["sub=game", "hostNot=admin", "id=page"]));
        }
    }

    [Test]
    public void Mint_ForAGivenNetwork_RefusesOneThatIsNotVersion8()
    {
        var v8 = StyxToken.NewNetworkId();
        var (ok, output, _) = Run(["mint", "--network", v8.ToString()], Key);
        var (refused, _, _) = Run(["mint", "--network", Guid.NewGuid().ToString()], Key);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ok, Is.Zero);
            Assert.That(StyxToken.Parse(output.Trim()).Network, Is.EqualTo(v8));
            Assert.That(refused, Is.EqualTo(2));
        }
    }

    [Test]
    public void Mint_WithAServer_PrintsABlobInTheCanonicalForm()
    {
        var (code, output, _) = Run(["mint", "--server", "https://styx.example.org", "--encryption-key", "k+y", "--max-blob", "511"], Key);
        var blob = StyxBlob.Parse(output.Trim());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(code, Is.Zero);
            Assert.That(blob.Write(), Is.EqualTo(output.Trim()));
            Assert.That(blob.EncryptionKey, Is.EqualTo("k+y"));
            Assert.That(StyxToken.Parse(blob.Authorization).Verify(StyxTestServer.TestKey), Is.True);
        }
    }

    [Test]
    public void Mint_WithAServer_MakesAFreshEncryptionKeyWhenNoneIsGiven()
    {
        var (_, output, _) = Run(["mint", "--server", "https://s"], Key);
        Assert.That(StyxBlob.Parse(output.Trim()).EncryptionKey, Has.Length.EqualTo(128));
    }

    [Test]
    public void Mint_RefusesToExceedTheLimitsItIsGiven()
    {
        var (tooLong, _, _) = Run(["mint", "--id", new string('a', 72), "--max-length", "100"], Key);
        var (tooBig, _, _) = Run(["mint", "--server", "https://s", "--max-blob", "100"], Key);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tooLong, Is.EqualTo(1));
            Assert.That(tooBig, Is.EqualTo(1));
        }
    }

    [Test]
    public void Mint_RefusesThePublishedTestKey_AndANoKeyEnvironment()
    {
        var published = Convert.ToBase64String([.. Enumerable.Range(0, 64).Select(i => (byte)i)]);
        var (withTestKey, _, _) = Run(["mint"], published);
        var (withoutKey, _, _) = Run(["mint"]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(withTestKey, Is.EqualTo(1));
            Assert.That(withoutKey, Is.EqualTo(1));
        }
    }

    private const string Passphrase = "a long sentence that nobody has ever written down before, about relays";

    [Test]
    public void Derive_PrintsThePassphrasesKey_FromStandardInput()
    {
        var (code, output, _) = Run(["derive"], input: Passphrase + "\n");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(code, Is.Zero);
            Assert.That(Convert.FromBase64String(output.Trim()), Is.EqualTo(StyxToken.KeyFromPassphrase(Passphrase)));
        }
    }

    [Test]
    public void Derive_RefusesAShortPassphrase()
    {
        var (code, _, error) = Run(["derive"], input: "short\n");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(code, Is.EqualTo(1));
            Assert.That(error, Does.Contain("64 characters"));
        }
    }

    [Test]
    public void Derive_SaysSo_WhenStandardInputIsEmpty()
    {
        var (code, _, error) = Run(["derive"]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(code, Is.EqualTo(1));
            Assert.That(error, Does.Contain("standard input"));
        }
    }

    [Test]
    public void Mint_SignsWithThePassphrasesKey()
    {
        var (code, output, _) = Run(["mint"], passphrase: Passphrase);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(code, Is.Zero);
            Assert.That(StyxToken.Parse(output.Trim()).Verify(StyxToken.KeyFromPassphrase(Passphrase)), Is.True);
        }
    }

    [TestCase("mint", "--bogus", "x")]
    [TestCase("mint", "--sub")]
    [TestCase("mint", "--sub", "a.b")]
    [TestCase("keygen", "extra")]
    [TestCase("derive", "extra")]
    public void BadArguments_AreRefused(params string[] args)
    {
        var (code, _, error) = Run(args, Key);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(code, Is.Not.Zero);
            Assert.That(error, Is.Not.Empty);
        }
    }

    private static (int Code, string Output, string Error) Run(string[] args, string? key = null, string? passphrase = null, string input = "")
    {
        var output = new StringWriter();
        var error = new StringWriter();
        string? Environment(string name) => name == Constants.RelayKeyEnvVar ? key : name == Constants.RelayKeyPassphraseEnvVar ? passphrase : null;
        var code = StyxTools.Run(args, Environment, new StringReader(input), output, error);
        return (code, output.ToString(), error.ToString());
    }
}
