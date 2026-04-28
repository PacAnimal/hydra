using Hydra.Keyboard;

namespace Tests.Keyboard;

[TestFixture]
public class ClassifyCharTests
{
    [TestCase('a')]
    [TestCase('Z')]
    [TestCase('0')]
    [TestCase(' ')]
    [TestCase('@')]
    [TestCase('€')]
    public void PrintableChar_ReturnsCharacter(char c)
    {
        var (ch, key) = KeyResolver.ClassifyChar(c);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ch, Is.EqualTo(c));
            Assert.That(key, Is.Null);
        }
    }

    [TestCase((char)8, SpecialKey.BackSpace)]
    [TestCase((char)9, SpecialKey.Tab)]
    [TestCase((char)13, SpecialKey.Return)]
    [TestCase((char)27, SpecialKey.Escape)]
    [TestCase((char)127, SpecialKey.Delete)]
    public void ControlChar_ReturnsMappedSpecialKey(char c, SpecialKey expected)
    {
        var (ch, key) = KeyResolver.ClassifyChar(c);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ch, Is.Null);
            Assert.That(key, Is.EqualTo(expected));
        }
    }

    [TestCase((char)0)]
    [TestCase((char)1)]
    [TestCase((char)3)]
    [TestCase((char)7)]
    [TestCase((char)10)]
    [TestCase((char)31)]
    public void UnmappedControlChar_ReturnsBothNull(char c)
    {
        var (ch, key) = KeyResolver.ClassifyChar(c);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ch, Is.Null);
            Assert.That(key, Is.Null);
        }
    }
}
