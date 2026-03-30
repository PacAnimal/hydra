using Hydra.Keyboard;
using Hydra.Platform.MacOs;

namespace Tests.Keyboard;

[TestFixture]
public class UnicodeToKeyIdTests
{
    [TestCase('a', (uint)'a')]
    [TestCase('Z', (uint)'Z')]
    [TestCase('0', (uint)'0')]
    [TestCase(' ', (uint)' ')]
    [TestCase('@', (uint)'@')]
    [TestCase('€', (uint)'€')]
    public void PrintableChar_ReturnsSelf(char c, uint expected) =>
        Assert.That(MacKeyResolver.UnicodeToKeyId(c), Is.EqualTo(expected));

    [TestCase((char)8, KeyId.BackSpace)]
    [TestCase((char)9, KeyId.Tab)]
    [TestCase((char)13, KeyId.Return)]
    [TestCase((char)27, KeyId.Escape)]
    [TestCase((char)127, KeyId.Delete)]
    [TestCase((char)3, KeyId.KP_Enter)]
    public void ControlChar_ReturnsMappedKeyId(char c, uint expected) =>
        Assert.That(MacKeyResolver.UnicodeToKeyId(c), Is.EqualTo(expected));

    [TestCase((char)0)]
    [TestCase((char)1)]
    [TestCase((char)7)]
    [TestCase((char)10)]
    [TestCase((char)31)]
    public void UnmappedControlChar_ReturnsNone(char c) =>
        Assert.That(MacKeyResolver.UnicodeToKeyId(c), Is.EqualTo(KeyId.None));
}
