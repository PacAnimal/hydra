using System.Security.Cryptography;
using Cathedral.Styx;
using Styx;

namespace Tests.Styx;

/// <summary>What a relay reads from its environment, and what it refuses to start with.</summary>
[TestFixture]
public class StyxEnvironmentTests
{
    private static readonly byte[] KeyA = SHA512.HashData("key a"u8);

    [Test]
    public void OneKey_AndThePassword_CreatingNetworks()
    {
        var auth = StyxEnvironment.Read(Env(("RELAY_SIGNING_KEY", Convert.ToBase64String(KeyA)), ("RELAY_PASSWORD", "pw")));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(auth.Keys, Has.Count.EqualTo(1));
            Assert.That(auth.Keys[0], Is.EqualTo(KeyA));
            Assert.That(auth.V1Password, Is.EqualTo("pw"));
            Assert.That(auth.AcceptsV1, Is.True);
            Assert.That(auth.CanMint, Is.True);
            Assert.That(auth.CreatesNetworks, Is.True);
        }
    }

    [Test]
    public void Revocations_AreRead_AndNoPasswordCreatesNoNetworks()
    {
        var revoked = Guid.NewGuid();
        var auth = StyxEnvironment.Read(Env(
            ("RELAY_SIGNING_KEY", Convert.ToBase64String(KeyA)),
            ("RELAY_REVOKED_NETWORKS", $" {revoked} ,"),
            ("RELAY_REVOKED_IDS", "a, b")));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(auth.AcceptsV1, Is.False);
            Assert.That(auth.CreatesNetworks, Is.False);
            Assert.That(auth.RevokedNetworks, Has.Count.EqualTo(1).And.Contains(revoked));
            Assert.That(auth.RevokedIds, Has.Count.EqualTo(2).And.Contains("a").And.Contains("b"));
        }
    }

    [Test]
    public void DuringARotation_SeveralKeys_TheFirstMinting()
    {
        var keyB = DistinctKidFrom(KeyA);
        var auth = StyxEnvironment.Read(Env(("RELAY_SIGNING_KEY", $"{Convert.ToBase64String(keyB)},{Convert.ToBase64String(KeyA)}")));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(auth.Keys[0], Is.EqualTo(keyB));
            Assert.That(auth.KeyFor(StyxToken.KeyIdOf(KeyA)), Is.EqualTo(KeyA));
        }
    }

    [Test]
    public void NothingToVerifyWith_RefusesToStart()
    {
        Assert.Throws<StyxConfigurationException>(() => StyxEnvironment.Read(Env()));
    }

    [Test]
    public void TheKeyPublishedInStyxMd_RefusesToStart()
    {
        var published = Convert.ToBase64String([.. Enumerable.Range(0, 64).Select(i => (byte)i)]);
        Assert.Throws<StyxConfigurationException>(() => StyxEnvironment.Read(Env(("RELAY_SIGNING_KEY", published))));
    }

    [TestCase("not base64!")]
    [TestCase("AAAA")]
    public void AMalformedKey_RefusesToStart(string key)
    {
        Assert.Throws<StyxConfigurationException>(() => StyxEnvironment.Read(Env(("RELAY_SIGNING_KEY", key))));
    }

    [Test]
    public void TheSameKeyTwice_RefusesToStart()
    {
        var key = Convert.ToBase64String(KeyA);
        Assert.Throws<StyxConfigurationException>(() => StyxEnvironment.Read(Env(("RELAY_SIGNING_KEY", $"{key},{key}"))));
    }

    [Test]
    public void TwoKeysSharingAKeyId_RefuseToStart()
    {
        var clash = SameKidAs(KeyA);
        Assert.Throws<StyxConfigurationException>(() =>
            StyxEnvironment.Read(Env(("RELAY_SIGNING_KEY", $"{Convert.ToBase64String(KeyA)},{Convert.ToBase64String(clash)}"))));
    }

    private const string Passphrase = "a long sentence that nobody has ever written down before, about relays";

    [Test]
    public void ARefusal_NamesTheSettingThatCausedIt()
    {
        var derived = Convert.ToBase64String(StyxToken.KeyFromPassphrase(Passphrase));
        var duplicate = Assert.Throws<StyxConfigurationException>(() => StyxEnvironment.Read(Env(("RELAY_SIGNING_PASSPHRASE", Passphrase), ("RELAY_SIGNING_KEY", derived))));
        Assert.That(duplicate!.Message, Does.StartWith("RELAY_SIGNING_KEY "));
    }

    [Test]
    public void APassphrase_DerivesTheFirstKey_AndSigningKeysOnlyVerifyBehindIt()
    {
        var auth = StyxEnvironment.Read(Env(("RELAY_SIGNING_PASSPHRASE", Passphrase), ("RELAY_SIGNING_KEY", Convert.ToBase64String(KeyA))));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(auth.Keys, Has.Count.EqualTo(2));
            Assert.That(auth.Keys[0], Is.EqualTo(StyxToken.KeyFromPassphrase(Passphrase)));
            Assert.That(auth.Keys[1], Is.EqualTo(KeyA));
        }
    }

    [TestCase("too short to ever be a relay key")]
    [TestCase(" a long sentence that nobody has ever written down before, about relays")]
    [TestCase("a long sentence that nobody has ever written down before, about relays\n")]
    [TestCase("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    public void APassphraseOutsideTheRules_RefusesToStart_NamingTheSetting(string passphrase)
    {
        var refused = Assert.Throws<StyxConfigurationException>(() => StyxEnvironment.Read(Env(("RELAY_SIGNING_PASSPHRASE", passphrase))));
        Assert.That(refused!.Message, Does.StartWith("RELAY_SIGNING_PASSPHRASE: "));
    }

    [Test]
    public void APassphraseAndItsOwnKey_AreTheSameKeyTwice()
    {
        var derived = Convert.ToBase64String(StyxToken.KeyFromPassphrase(Passphrase));
        Assert.Throws<StyxConfigurationException>(() => StyxEnvironment.Read(Env(("RELAY_SIGNING_PASSPHRASE", Passphrase), ("RELAY_SIGNING_KEY", derived))));
    }

    [Test]
    public void AKeyOfTheOldLength_RefusesToStart()
    {
        Assert.Throws<StyxConfigurationException>(() => StyxEnvironment.Read(Env(("RELAY_SIGNING_KEY", Convert.ToBase64String(SHA256.HashData("key a"u8))))));
    }

    [Test]
    public void ARevokedNetworkThatIsNotAUuid_RefusesToStart()
    {
        Assert.Throws<StyxConfigurationException>(() =>
            StyxEnvironment.Read(Env(("RELAY_PASSWORD", "pw"), ("RELAY_REVOKED_NETWORKS", "nope"))));
    }

    [Test]
    public void ParallelInvocations_DefaultToFour() =>
        Assert.That(StyxEnvironment.ReadParallelInvocations(Env()), Is.EqualTo(4));

    [TestCase("1", 1)]
    [TestCase("8", 8)]
    [TestCase("64", 64)]
    public void ParallelInvocations_AreReadFromOneToSixtyFour(string value, int expected) =>
        Assert.That(StyxEnvironment.ReadParallelInvocations(Env(("RELAY_PARALLEL_INVOCATIONS", value))), Is.EqualTo(expected));

    [TestCase("0")]
    [TestCase("-1")]
    [TestCase("65")]
    [TestCase("four")]
    [TestCase(" 4")]
    [TestCase("4.0")]
    [TestCase("99999999999")]
    public void ParallelInvocations_OutsideOneToSixtyFour_AreRefused(string value)
    {
        var refused = Assert.Throws<StyxConfigurationException>(() => StyxEnvironment.ReadParallelInvocations(Env(("RELAY_PARALLEL_INVOCATIONS", value))));
        Assert.That(refused!.Message, Does.Contain("RELAY_PARALLEL_INVOCATIONS"));
    }

    [Test]
    public void LocalPort_DefaultsTo5000() =>
        Assert.That(StyxEnvironment.ReadLocalPort(Env()), Is.EqualTo(5000));

    [TestCase("1", 1)]
    [TestCase("8080", 8080)]
    [TestCase("65535", 65535)]
    public void LocalPort_IsRead(string value, int expected) =>
        Assert.That(StyxEnvironment.ReadLocalPort(Env(("LOCAL_PORT", value))), Is.EqualTo(expected));

    [TestCase("0")]
    [TestCase("65536")]
    [TestCase("http")]
    [TestCase("-80")]
    public void LocalPort_ThatIsNotAPort_IsRefused(string value)
    {
        var refused = Assert.Throws<StyxConfigurationException>(() => StyxEnvironment.ReadLocalPort(Env(("LOCAL_PORT", value))));
        Assert.That(refused!.Message, Does.Contain("LOCAL_PORT"));
    }

    [TestCase(null, false)]
    [TestCase("true", true)]
    [TestCase("TRUE", true)]
    [TestCase("1", false)]
    public void Flags_AreOnlyOnForTrue(string? value, bool expected) =>
        Assert.That(StyxEnvironment.ReadFlag(Env(("LOCAL_ONLY", value!)), "LOCAL_ONLY"), Is.EqualTo(expected));

    [TestCase(2)]
    [TestCase(4)]
    [TestCase(8)]
    public void ParallelInvocations_FromTwoToEight_StartWithoutAWarning(int width) =>
        Assert.That(StyxEnvironment.ParallelInvocationsWarning(width), Is.Null);

    [Test]
    public void ParallelInvocations_OfOne_WarnThePathIsNotParallel() =>
        Assert.That(StyxEnvironment.ParallelInvocationsWarning(1), Does.Contain("not parallel"));

    [TestCase(9, 288)]
    [TestCase(64, 2048)]
    public void ParallelInvocations_AboveEight_WarnOfTheMemoryOneClientCanHold(int width, int mebibytes) =>
        Assert.That(StyxEnvironment.ParallelInvocationsWarning(width), Does.Contain($"{mebibytes} MiB"));

    private static Func<string, string?> Env(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(v => v.Name == name).Value;

    // deterministic: hash a counter until a key's id does or does not collide
    private static byte[] SameKidAs(byte[] key) => Search(candidate => StyxToken.KeyIdOf(candidate) == StyxToken.KeyIdOf(key) && !candidate.SequenceEqual(key));

    private static byte[] DistinctKidFrom(byte[] key) => Search(candidate => StyxToken.KeyIdOf(candidate) != StyxToken.KeyIdOf(key));

    private static byte[] Search(Func<byte[], bool> wanted)
    {
        for (var i = 0; ; i++)
        {
            var candidate = SHA512.HashData(BitConverter.GetBytes(i));
            if (wanted(candidate)) return candidate;
        }
    }
}
