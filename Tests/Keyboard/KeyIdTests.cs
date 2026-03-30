using Hydra.Keyboard;

namespace Tests.Keyboard;

[TestFixture]
public class KeyIdTests
{
    // -- IsPrintable --

    [TestCase('a', ExpectedResult = true)]
    [TestCase('Z', ExpectedResult = true)]
    [TestCase('0', ExpectedResult = true)]
    [TestCase(' ', ExpectedResult = true)]
    [TestCase('@', ExpectedResult = true)]
    public bool IsPrintable_AsciiChar(char c) => KeyId.IsPrintable(c);

    [Test]
    public void IsPrintable_None_ReturnsFalse() =>
        Assert.That(KeyId.IsPrintable(KeyId.None), Is.False);

    [TestCase(KeyId.Left)]
    [TestCase(KeyId.Up)]
    [TestCase(KeyId.F1)]
    [TestCase(KeyId.F16)]
    [TestCase(KeyId.Escape)]
    [TestCase(KeyId.BackSpace)]
    [TestCase(KeyId.Shift_L)]
    public void IsPrintable_SpecialKey_ReturnsFalse(uint id) =>
        Assert.That(KeyId.IsPrintable(id), Is.False);

    // keypad digits and equal count as printable (for AltGr detection)
    [TestCase(KeyId.KP_0)]
    [TestCase(KeyId.KP_9)]
    [TestCase(KeyId.KP_Equal)]
    public void IsPrintable_KeypadDigits_ReturnsTrue(uint id) =>
        Assert.That(KeyId.IsPrintable(id), Is.True);

    // -- IsModifier --

    [TestCase(KeyId.Shift_L)]
    [TestCase(KeyId.Shift_R)]
    [TestCase(KeyId.Control_L)]
    [TestCase(KeyId.Control_R)]
    [TestCase(KeyId.Alt_L)]
    [TestCase(KeyId.Alt_R)]
    [TestCase(KeyId.Super_L)]
    [TestCase(KeyId.Super_R)]
    [TestCase(KeyId.CapsLock)]
    public void IsModifier_ModifierKey_ReturnsTrue(uint id) =>
        Assert.That(KeyId.IsModifier(id), Is.True);

    [TestCase(KeyId.Return)]
    [TestCase(KeyId.F1)]
    [TestCase((uint)'a')]
    public void IsModifier_NonModifier_ReturnsFalse(uint id) =>
        Assert.That(KeyId.IsModifier(id), Is.False);

    // -- constant values --

    [Test]
    public void PrintableAscii_EqualsUnicodeCodepoint()
    {
        using (Assert.EnterMultipleScope())
        {
            // printable ASCII chars ARE their unicode codepoint
            Assert.That((uint)'A', Is.EqualTo(0x0041u));
            Assert.That((uint)'a', Is.EqualTo(0x0061u));
            Assert.That((uint)'0', Is.EqualTo(0x0030u));
        }
    }

    [Test]
    public void SpecialKeys_HaveExpectedValues()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(KeyId.BackSpace, Is.EqualTo(0xEF08u));
            Assert.That(KeyId.Tab, Is.EqualTo(0xEF09u));
            Assert.That(KeyId.Return, Is.EqualTo(0xEF0Du));
            Assert.That(KeyId.Escape, Is.EqualTo(0xEF1Bu));
            Assert.That(KeyId.Left, Is.EqualTo(0xEF51u));
            Assert.That(KeyId.F1, Is.EqualTo(0xEFBEu));
            Assert.That(KeyId.F16, Is.EqualTo(0xEFCDu));
            Assert.That(KeyId.Shift_L, Is.EqualTo(0xEFE1u));
            Assert.That(KeyId.Delete, Is.EqualTo(0xEFFFu));
        }
    }

    [Test]
    public void FunctionKeys_AreConsecutive()
    {
        for (uint i = 0; i < 16; i++)
        {
            var expected = KeyId.F1 + i;
            var actual = i switch
            {
                0 => KeyId.F1,
                1 => KeyId.F2,
                2 => KeyId.F3,
                3 => KeyId.F4,
                4 => KeyId.F5,
                5 => KeyId.F6,
                6 => KeyId.F7,
                7 => KeyId.F8,
                8 => KeyId.F9,
                9 => KeyId.F10,
                10 => KeyId.F11,
                11 => KeyId.F12,
                12 => KeyId.F13,
                13 => KeyId.F14,
                14 => KeyId.F15,
                15 => KeyId.F16,
                _ => 0u,
            };
            Assert.That(actual, Is.EqualTo(expected), $"F{i + 1}");
        }
    }
}
