using Microsoft.Extensions.Time.Testing;
using Styx.Services;

namespace Tests.Styx;

[TestFixture]
public class FailureLimiterTests
{
    [Test]
    public void AnAddress_IsBlockedAfterTheLimit_UntilTheMinutePasses()
    {
        var clock = new FakeTimeProvider();
        var limiter = new FailureLimiter(clock);
        for (var i = 0; i < FailureLimiter.MaxFailures - 1; i++) limiter.RecordFailure("203.0.113.7");
        Assert.That(limiter.IsBlocked("203.0.113.7"), Is.False);

        limiter.RecordFailure("203.0.113.7");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(limiter.IsBlocked("203.0.113.7"), Is.True);
            Assert.That(limiter.IsBlocked("203.0.113.8"), Is.False);
        }

        clock.Advance(FailureLimiter.Window);
        Assert.That(limiter.IsBlocked("203.0.113.7"), Is.False);
    }

    [Test]
    public void IPv6_IsKeyedByItsSlash64_AndMappedIPv4_ByItsIPv4()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(FailureLimiter.KeyOf("2001:db8:1:2:aaaa::1"), Is.EqualTo(FailureLimiter.KeyOf("2001:db8:1:2:bbbb::2")));
            Assert.That(FailureLimiter.KeyOf("2001:db8:1:3::1"), Is.Not.EqualTo(FailureLimiter.KeyOf("2001:db8:1:2::1")));
            Assert.That(FailureLimiter.KeyOf("::ffff:203.0.113.7"), Is.EqualTo("203.0.113.7"));
        }
    }
}
