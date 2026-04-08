using Hydra.Keyboard;
using Hydra.Platform.Linux;

namespace Tests.Keyboard;

[TestFixture]
public class XorgSpecialKeyMapTests
{
    [TestCase(0xFFBEUL, SpecialKey.F1)]
    [TestCase(0xFFBFUL, SpecialKey.F2)]
    [TestCase(0xFFC2UL, SpecialKey.F5)]
    [TestCase(0xFFC7UL, SpecialKey.F10)]
    [TestCase(0xFFC9UL, SpecialKey.F12)]
    [TestCase(0xFFCAUL, SpecialKey.F13)]
    [TestCase(0xFFCDUL, SpecialKey.F16)]
    public void FunctionKeys_AreMapped(ulong keysym, SpecialKey expected)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(XorgSpecialKeyMap.Instance.TryGet(keysym, out var key), Is.True);
            Assert.That(key, Is.EqualTo(expected));
        }
    }

    [TestCase(0xFF51UL, SpecialKey.Left)]
    [TestCase(0xFF53UL, SpecialKey.Right)]
    [TestCase(0xFF52UL, SpecialKey.Up)]
    [TestCase(0xFF54UL, SpecialKey.Down)]
    [TestCase(0xFF50UL, SpecialKey.Home)]
    [TestCase(0xFF57UL, SpecialKey.End)]
    [TestCase(0xFF55UL, SpecialKey.PageUp)]
    [TestCase(0xFF56UL, SpecialKey.PageDown)]
    [TestCase(0xFF63UL, SpecialKey.Insert)]
    public void NavigationKeys_AreMapped(ulong keysym, SpecialKey expected)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(XorgSpecialKeyMap.Instance.TryGet(keysym, out var key), Is.True);
            Assert.That(key, Is.EqualTo(expected));
        }
    }

    [TestCase(0xFFE1UL, SpecialKey.Shift_L)]
    [TestCase(0xFFE2UL, SpecialKey.Shift_R)]
    [TestCase(0xFFE3UL, SpecialKey.Control_L)]
    [TestCase(0xFFE4UL, SpecialKey.Control_R)]
    [TestCase(0xFFE5UL, SpecialKey.CapsLock)]
    [TestCase(0xFFE9UL, SpecialKey.Alt_L)]
    [TestCase(0xFFEAUL, SpecialKey.Alt_R)]
    [TestCase(0xFFEBUL, SpecialKey.Super_L)]
    [TestCase(0xFFECUL, SpecialKey.Super_R)]
    [TestCase(0xFE03UL, SpecialKey.AltGr)]
    public void ModifierKeys_AreMapped(ulong keysym, SpecialKey expected)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(XorgSpecialKeyMap.Instance.TryGet(keysym, out var key), Is.True);
            Assert.That(key, Is.EqualTo(expected));
        }
    }

    [TestCase(0x1008FF12UL, SpecialKey.AudioMute)]
    [TestCase(0x1008FF11UL, SpecialKey.AudioVolumeDown)]
    [TestCase(0x1008FF13UL, SpecialKey.AudioVolumeUp)]
    [TestCase(0x1008FF17UL, SpecialKey.AudioNext)]
    [TestCase(0x1008FF16UL, SpecialKey.AudioPrev)]
    [TestCase(0x1008FF15UL, SpecialKey.AudioStop)]
    [TestCase(0x1008FF14UL, SpecialKey.AudioPlay)]
    [TestCase(0x1008FF02UL, SpecialKey.BrightnessUp)]
    [TestCase(0x1008FF03UL, SpecialKey.BrightnessDown)]
    [TestCase(0x1008FF2CUL, SpecialKey.Eject)]
    public void MediaKeys_AreMapped(ulong keysym, SpecialKey expected)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(XorgSpecialKeyMap.Instance.TryGet(keysym, out var key), Is.True);
            Assert.That(key, Is.EqualTo(expected));
        }
    }

    [Test]
    public void UnknownKeysym_ReturnsFalse()
    {
        // 0x0061 (XK_a) is a character key, not in the special map
        Assert.That(XorgSpecialKeyMap.Instance.TryGet(0x0061, out _), Is.False);
    }

    [Test]
    public void NoDuplicateSpecialKeys()
    {
        // ISO_Left_Tab intentionally shares SpecialKey.Tab with Tab (Shift+Tab alias)
        var unintentionalDupes = XorgSpecialKeyMap.Instance.Entries
            .GroupBy(kvp => kvp.Value)
            .Where(g => g.Count() > 1 && g.Key != SpecialKey.Tab)
            .ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(unintentionalDupes, Is.Empty);
            Assert.That(XorgSpecialKeyMap.Instance.Reverse, Is.Not.Empty);
        }
    }
}
