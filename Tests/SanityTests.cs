namespace Tests;

[TestFixture]
public class SanityTests
{
    [Test]
    public void Sanity()
    {
        var x = 2;
        Assert.That(x, Is.EqualTo(2));
    }
}
