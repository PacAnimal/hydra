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

    [Test]
    public void TheTable_IsBounded()
    {
        var lru = new BoundedLru<int, int>(3);
        for (var i = 0; i < 10; i++) lru.Set(i, i);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(lru.Count, Is.EqualTo(3));
            Assert.That(lru.TryGet(0, out _), Is.False);
            Assert.That(lru.TryGet(9, out var nine) && nine == 9, Is.True);
        }
    }

    [Test]
    public void TheLeastRecentlyUsed_IsWhatGoes()
    {
        var lru = new BoundedLru<string, int>(2);
        lru.Set("a", 1);
        lru.Set("b", 2);
        lru.TryGet("a", out _);
        lru.Set("c", 3);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(lru.TryGet("a", out _), Is.True);
            Assert.That(lru.TryGet("b", out _), Is.False);
        }
    }
}
