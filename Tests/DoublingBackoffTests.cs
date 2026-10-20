using Hydra;

namespace Tests;

[TestFixture]
public class DoublingBackoffTests
{
    [Test]
    public void Next_DoublesUpToTheCap()
    {
        var backoff = new DoublingBackoff(500, 3_000);
        Assert.That(new[] { backoff.Next(), backoff.Next(), backoff.Next(), backoff.Next(), backoff.Next() },
            Is.EqualTo([500, 1_000, 2_000, 3_000, 3_000]));
    }

    [Test]
    public void Reset_StartsOver()
    {
        var backoff = new DoublingBackoff(500, 3_000);
        backoff.Next();
        backoff.Next();
        backoff.Reset();
        Assert.That(backoff.Next(), Is.EqualTo(500));
    }
}
