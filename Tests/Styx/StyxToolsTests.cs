using Cathedral.Styx;
using Styx;
using Tests.Setup;

namespace Tests.Styx;

/// <summary>The relay binary's <c>keygen</c> and <c>mint</c> subcommands.</summary>
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
            Assert.That(StyxEnvironment.ReadKeys(key), Has.Count.EqualTo(1));
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
        var published = Convert.ToBase64String([.. Enumerable.Range(0, 32).Select(i => (byte)i)]);
        var (withTestKey, _, _) = Run(["mint"], published);
        var (withoutKey, _, _) = Run(["mint"]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(withTestKey, Is.EqualTo(1));
            Assert.That(withoutKey, Is.EqualTo(1));
        }
    }

    [TestCase("mint", "--bogus", "x")]
    [TestCase("mint", "--sub")]
    [TestCase("mint", "--sub", "a.b")]
    [TestCase("keygen", "extra")]
    public void BadArguments_AreRefused(params string[] args)
    {
        var (code, _, error) = Run(args, Key);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(code, Is.Not.Zero);
            Assert.That(error, Is.Not.Empty);
        }
    }

    private static (int Code, string Output, string Error) Run(string[] args, string? key = null)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = StyxTools.Run(args, name => name == Constants.RelayKeyEnvVar ? key : null, output, error);
        return (code, output.ToString(), error.ToString());
    }
}
