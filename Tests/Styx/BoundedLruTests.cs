using Common;

namespace Tests.Styx;

[TestFixture]
public class BoundedLruTests
{
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
