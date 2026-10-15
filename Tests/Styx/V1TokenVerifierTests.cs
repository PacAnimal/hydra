using Cathedral.Styx;
using Cathedral.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Styx;
using Styx.Services;
using Tests.Setup;

namespace Tests.Styx;

/// <summary>The v1 path never pays a PBKDF2 for nothing, and never admits to a v2 network.</summary>
[TestFixture]
public class V1TokenVerifierTests
{
    private const string Ip = "203.0.113.7";

    [Test]
    public async Task AValidToken_NamesItsNetwork_AndIsAnsweredFromTheCacheAfterwards()
    {
        var (verifier, _) = Verifier();
        var network = Guid.NewGuid();
        var token = await StyxTestServer.GenerateAuthorization(network);

        var first = await verifier.Verify(token, Ip, CancellationToken.None);
        var second = await verifier.Verify(token, Ip, CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.EqualTo(network));
            Assert.That(second, Is.EqualTo(network));
            Assert.That(verifier.DerivationCount, Is.EqualTo(1));
        }
    }

    [TestCase("")]
    [TestCase("not base64!")]
    [TestCase("AAAA")]
    public async Task Junk_IsRefusedWithoutADerivation(string junk)
    {
        var (verifier, limiter) = Verifier();
        Assert.That(await verifier.Verify(junk, Ip, CancellationToken.None), Is.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(verifier.DerivationCount, Is.Zero);
            Assert.That(limiter.IsBlocked(Ip), Is.False);
        }
    }

    [Test]
    public async Task ANonCanonicalSpelling_IsRefusedWithoutADerivation()
    {
        var (verifier, _) = Verifier();
        var token = await StyxTestServer.GenerateAuthorization(Guid.NewGuid());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await verifier.Verify(" " + token, Ip, CancellationToken.None), Is.Null);
            Assert.That(verifier.DerivationCount, Is.Zero);
        }
    }

    [Test]
    public async Task AWellFormedForgery_CostsOneDerivation_AndCountsTowardTheLimit()
    {
        var (verifier, limiter) = Verifier();
        var forged = Convert.ToBase64String(new byte[V1TokenVerifier.TokenBytes]);
        for (var i = 0; i < FailureLimiter.MaxFailures; i++)
            Assert.That(await verifier.Verify(forged, Ip, CancellationToken.None), Is.Null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verifier.DerivationCount, Is.EqualTo(FailureLimiter.MaxFailures));
            Assert.That(limiter.IsBlocked(Ip), Is.True);
        }

        // blocked: refused without deriving, even for a valid token; a cache hit is still answered
        var valid = await StyxTestServer.GenerateAuthorization(Guid.NewGuid());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await verifier.Verify(valid, Ip, CancellationToken.None), Is.Null);
            Assert.That(verifier.DerivationCount, Is.EqualTo(FailureLimiter.MaxFailures));
        }
    }

    [Test]
    public async Task ACacheHit_IsAnsweredEvenWhileTheAddressIsBlocked()
    {
        var (verifier, limiter) = Verifier();
        var network = Guid.NewGuid();
        var token = await StyxTestServer.GenerateAuthorization(network);
        await verifier.Verify(token, Ip, CancellationToken.None);
        for (var i = 0; i < FailureLimiter.MaxFailures; i++) limiter.RecordFailure(Ip);

        Assert.That(await verifier.Verify(token, Ip, CancellationToken.None), Is.EqualTo(network));
    }

    [Test]
    public async Task AVersion8Network_IsNeverAdmitted_AndDoesNotCountAsAGuess()
    {
        var (verifier, limiter) = Verifier();
        var token = await new SimpleAes(StyxTestServer.TestPassword).EncryptBase64(StyxToken.NewNetworkId(), CancellationToken.None);
        for (var i = 0; i < FailureLimiter.MaxFailures + 1; i++)
            Assert.That(await verifier.Verify(token, Ip, CancellationToken.None), Is.Null);
        Assert.That(limiter.IsBlocked(Ip), Is.False);
    }

    [Test]
    public async Task WithoutAPassword_NothingIsAdmitted()
    {
        var (verifier, _) = Verifier(password: null);
        var token = await StyxTestServer.GenerateAuthorization(Guid.NewGuid());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await verifier.Verify(token, Ip, CancellationToken.None), Is.Null);
            Assert.That(verifier.DerivationCount, Is.Zero);
        }
    }

    [Test]
    public async Task ARevokedNetwork_IsRefusedOnACacheHit()
    {
        var network = Guid.NewGuid();
        var token = await StyxTestServer.GenerateAuthorization(network);
        var auth = new StyxAuth([], StyxTestServer.TestPassword, null, new HashSet<Guid> { network }, new HashSet<string>());
        var clock = new FakeTimeProvider();
        var v1 = new V1TokenVerifier(auth, new FailureLimiter(clock), clock, NullLogger<V1TokenVerifier>.Instance);
        var verifier = new TokenVerifier(auth, v1, clock);

        await v1.Verify(token, Ip, CancellationToken.None); // proven, and cached
        Assert.That((await verifier.Verify(token, Ip, CancellationToken.None)).Network, Is.Null);
    }

    [Test]
    public async Task WhenEveryDerivationSlotIsTaken_ALoginGivesUpWithinTheBoundedWait()
    {
        var (verifier, limiter) = Verifier();
        var token = await StyxTestServer.GenerateAuthorization(Guid.NewGuid());
        var held = verifier.Derivations.CurrentCount;
        for (var i = 0; i < held; i++) await verifier.Derivations.WaitAsync();
        try
        {
            Assert.That(await verifier.Verify(token, Ip, CancellationToken.None), Is.Null);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(verifier.DerivationCount, Is.Zero);
                Assert.That(limiter.IsBlocked(Ip), Is.False, "a timed-out wait is not a failed guess");
            }
        }
        finally
        {
            verifier.Derivations.Release(held);
        }
    }

    [Test]
    public async Task AnAttemptQueuedForASlot_IsRefusedIfItsAddressIsBlockedMeanwhile()
    {
        var (verifier, limiter) = Verifier();
        var token = await StyxTestServer.GenerateAuthorization(Guid.NewGuid());
        var held = verifier.Derivations.CurrentCount;
        for (var i = 0; i < held; i++) await verifier.Derivations.WaitAsync();

        // whether the attempt is already queued or not when the address is blocked, it must not derive
        var attempt = verifier.Verify(token, Ip, CancellationToken.None);
        for (var i = 0; i < FailureLimiter.MaxFailures; i++) limiter.RecordFailure(Ip);
        verifier.Derivations.Release(held);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await attempt, Is.Null);
            Assert.That(verifier.DerivationCount, Is.Zero);
        }
    }

    [Test]
    public void ConcurrentFailures_AreAllCounted()
    {
        var limiter = new FailureLimiter(new FakeTimeProvider());
        Parallel.For(0, FailureLimiter.MaxFailures, _ => limiter.RecordFailure(Ip));
        Assert.That(limiter.IsBlocked(Ip), Is.True);
    }

    private static (V1TokenVerifier Verifier, FailureLimiter Limiter) Verifier(string? password = StyxTestServer.TestPassword)
    {
        var clock = new FakeTimeProvider();
        var limiter = new FailureLimiter(clock);
        var auth = new StyxAuth([], password, null, new HashSet<Guid>(), new HashSet<string>());
        return (new V1TokenVerifier(auth, limiter, clock, NullLogger<V1TokenVerifier>.Instance), limiter);
    }
}
