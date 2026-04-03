using Hydra.Keyboard;
using Hydra.Platform.MacOs;

namespace Tests.Keyboard;

[TestFixture]
public class MacSpecialKeyMapTests
{
    [Test]
    public void NoDuplicateVirtualKeyCodes()
    {
        var map = MacSpecialKeyMap.All;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(map, Is.Not.Empty);
            // each SpecialKey should appear at most once; Reverse will have same count if no duplicates
            Assert.That(MacSpecialKeyMap.Reverse, Has.Count.EqualTo(map.Count));
        }
    }

    // kVK_* values from Carbon Events.h
    [TestCase(0x7A, SpecialKey.F1)]
    [TestCase(0x78, SpecialKey.F2)]
    [TestCase(0x60, SpecialKey.F5)]
    [TestCase(0x6D, SpecialKey.F10)]
    [TestCase(0x6F, SpecialKey.F12)]
    [TestCase(0x69, SpecialKey.F13)]
    [TestCase(0x6A, SpecialKey.F16)]
    public void FunctionKeys_AreMapped(int vk, SpecialKey expected)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(MacSpecialKeyMap.TryGet(vk, out var key), Is.True);
            Assert.That(key, Is.EqualTo(expected));
        }
    }

    [TestCase(0x7B, SpecialKey.Left)]
    [TestCase(0x7C, SpecialKey.Right)]
    [TestCase(0x7E, SpecialKey.Up)]
    [TestCase(0x7D, SpecialKey.Down)]
    [TestCase(0x73, SpecialKey.Home)]
    [TestCase(0x77, SpecialKey.End)]
    [TestCase(0x74, SpecialKey.PageUp)]
    [TestCase(0x79, SpecialKey.PageDown)]
    public void NavigationKeys_AreMapped(int vk, SpecialKey expected)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(MacSpecialKeyMap.TryGet(vk, out var key), Is.True);
            Assert.That(key, Is.EqualTo(expected));
        }
    }

    [TestCase(0x38, SpecialKey.Shift_L)]
    [TestCase(0x3C, SpecialKey.Shift_R)]
    [TestCase(0x3B, SpecialKey.Control_L)]
    [TestCase(0x3E, SpecialKey.Control_R)]
    [TestCase(0x3A, SpecialKey.Alt_L)]
    [TestCase(0x3D, SpecialKey.Alt_R)]
    [TestCase(0x37, SpecialKey.Super_L)]
    [TestCase(0x36, SpecialKey.Super_R)]
    [TestCase(0x39, SpecialKey.CapsLock)]
    public void ModifierKeys_AreMapped(int vk, SpecialKey expected)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(MacSpecialKeyMap.TryGet(vk, out var key), Is.True);
            Assert.That(key, Is.EqualTo(expected));
        }
    }

    [TestCase(0x52, SpecialKey.KP_0)]
    [TestCase(0x5C, SpecialKey.KP_9)]
    [TestCase(0x4C, SpecialKey.KP_Enter)]
    [TestCase(0x47, SpecialKey.NumLock)]  // keypad clear = numlock on mac
    public void KeypadKeys_AreMapped(int vk, SpecialKey expected)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(MacSpecialKeyMap.TryGet(vk, out var key), Is.True);
            Assert.That(key, Is.EqualTo(expected));
        }
    }

    [Test]
    public void UnknownVirtualKey_ReturnsFalse()
    {
        // 0x00 (kVK_ANSI_A) is a character key, not in the special map
        Assert.That(MacSpecialKeyMap.TryGet(0x00, out _), Is.False);
    }
}
