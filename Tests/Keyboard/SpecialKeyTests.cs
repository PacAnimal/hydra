using Hydra.Keyboard;

namespace Tests.Keyboard;

[TestFixture]
public class SpecialKeyTests
{
    // -- IsModifier --

    [TestCase(SpecialKey.Shift_L)]
    [TestCase(SpecialKey.Shift_R)]
    [TestCase(SpecialKey.Control_L)]
    [TestCase(SpecialKey.Control_R)]
    [TestCase(SpecialKey.Alt_L)]
    [TestCase(SpecialKey.Alt_R)]
    [TestCase(SpecialKey.Super_L)]
    [TestCase(SpecialKey.Super_R)]
    [TestCase(SpecialKey.CapsLock)]
    [TestCase(SpecialKey.AltGr)]
    [TestCase(SpecialKey.NumLock)]
    [TestCase(SpecialKey.ScrollLock)]
    public void IsModifier_ModifierKey_ReturnsTrue(SpecialKey key) =>
        Assert.That(key.IsModifier(), Is.True);

    [TestCase(SpecialKey.Return)]
    [TestCase(SpecialKey.F1)]
    [TestCase(SpecialKey.Left)]
    [TestCase(SpecialKey.AudioPlay)]
    [TestCase(SpecialKey.AudioVolumeUp)]
    public void IsModifier_NonModifier_ReturnsFalse(SpecialKey key) =>
        Assert.That(key.IsModifier(), Is.False);

    // -- enum values (encoding: keysym | 0x01000000) --

    [Test]
    public void SpecialKeys_HaveExpectedValues()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That((uint)SpecialKey.BackSpace, Is.EqualTo(0x01FF08u));
            Assert.That((uint)SpecialKey.Tab, Is.EqualTo(0x01FF09u));
            Assert.That((uint)SpecialKey.Return, Is.EqualTo(0x01FF0Du));
            Assert.That((uint)SpecialKey.Escape, Is.EqualTo(0x01FF1Bu));
            Assert.That((uint)SpecialKey.Left, Is.EqualTo(0x01FF51u));
            Assert.That((uint)SpecialKey.F1, Is.EqualTo(0x01FFBEu));
            Assert.That((uint)SpecialKey.F16, Is.EqualTo(0x01FFCDu));
            Assert.That((uint)SpecialKey.Shift_L, Is.EqualTo(0x01FFE1u));
            Assert.That((uint)SpecialKey.Delete, Is.EqualTo(0x01FFFFu));
            Assert.That((uint)SpecialKey.AltGr, Is.EqualTo(0x01FE03u));
        }
    }

    [Test]
    public void FunctionKeys_AreConsecutive()
    {
        for (uint i = 0; i < 20; i++)
        {
            var expected = (uint)SpecialKey.F1 + i;
            var actual = i switch
            {
                0 => SpecialKey.F1,
                1 => SpecialKey.F2,
                2 => SpecialKey.F3,
                3 => SpecialKey.F4,
                4 => SpecialKey.F5,
                5 => SpecialKey.F6,
                6 => SpecialKey.F7,
                7 => SpecialKey.F8,
                8 => SpecialKey.F9,
                9 => SpecialKey.F10,
                10 => SpecialKey.F11,
                11 => SpecialKey.F12,
                12 => SpecialKey.F13,
                13 => SpecialKey.F14,
                14 => SpecialKey.F15,
                15 => SpecialKey.F16,
                16 => SpecialKey.F17,
                17 => SpecialKey.F18,
                18 => SpecialKey.F19,
                19 => SpecialKey.F20,
                _ => throw new InvalidOperationException($"Unexpected: {i}"),
            };
            Assert.That((uint)actual, Is.EqualTo(expected), $"F{i + 1}");
        }
    }
}
