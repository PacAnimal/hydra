using Hydra;

namespace Tests;

[TestFixture]
public class ExceptionExtTests
{
    [Test]
    public void InnerMessage_PrefersTheInnerException() =>
        Assert.That(new HttpRequestException("outer", new IOException("inner")).InnerMessage(), Is.EqualTo("inner"));

    [Test]
    public void InnerMessage_FallsBackToTheException() =>
        Assert.That(new HttpRequestException("outer").InnerMessage(), Is.EqualTo("outer"));
}
