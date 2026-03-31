using Hydra.Keyboard;

namespace Tests.Keyboard;

[TestFixture]
public class KeyModifiersTests
{
    [Test]
    public void Modifiers_HaveExpectedBitValues()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That((uint)KeyModifiers.Shift, Is.EqualTo(0x0001u));
            Assert.That((uint)KeyModifiers.Control, Is.EqualTo(0x0002u));
            Assert.That((uint)KeyModifiers.Alt, Is.EqualTo(0x0004u));
            Assert.That((uint)KeyModifiers.Super, Is.EqualTo(0x0010u));
            Assert.That((uint)KeyModifiers.AltGr, Is.EqualTo(0x0020u));
            Assert.That((uint)KeyModifiers.CapsLock, Is.EqualTo(0x1000u));
            Assert.That((uint)KeyModifiers.NumLock, Is.EqualTo(0x2000u));
            Assert.That((uint)KeyModifiers.ScrollLock, Is.EqualTo(0x4000u));
        }
    }

    [Test]
    public void Modifiers_HaveNoDuplicateBits()
    {
        var values = new[]
        {
            KeyModifiers.Shift, KeyModifiers.Control, KeyModifiers.Alt,
            KeyModifiers.Super, KeyModifiers.AltGr,
            KeyModifiers.CapsLock, KeyModifiers.NumLock, KeyModifiers.ScrollLock,
        };

        uint seen = 0;
        foreach (var mod in values)
        {
            var bit = (uint)mod;
            Assert.That(seen & bit, Is.Zero, $"{mod} overlaps with a previous modifier");
            seen |= bit;
        }
    }

    [Test]
    public void Modifiers_CanBeCombined()
    {
        var ctrl = KeyModifiers.Control | KeyModifiers.Alt;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ctrl.HasFlag(KeyModifiers.Control), Is.True);
            Assert.That(ctrl.HasFlag(KeyModifiers.Alt), Is.True);
            Assert.That(ctrl.HasFlag(KeyModifiers.Shift), Is.False);
        }
    }
}
