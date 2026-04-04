using Hydra.Keyboard;
using Hydra.Platform.MacOs;

namespace Tests.Keyboard;

[TestFixture]
public class MacSpecialKeyMapTests
{
    [Test]
    public void NoDuplicateVirtualKeyCodes()
    {
        var map = MacSpecialKeyMap.Instance.All;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(map, Is.Not.Empty);
            // each SpecialKey should appear at most once; Reverse will have same count if no duplicates
            Assert.That(MacSpecialKeyMap.Instance.Reverse, Has.Count.EqualTo(map.Count));
        }
    }

    // kVK_* values from Carbon Events.h
    [TestCase(0x7AUL, SpecialKey.F1)]
    [TestCase(0x78UL, SpecialKey.F2)]
    [TestCase(0x60UL, SpecialKey.F5)]
    [TestCase(0x6DUL, SpecialKey.F10)]
    [TestCase(0x6FUL, SpecialKey.F12)]
    [TestCase(0x69UL, SpecialKey.F13)]
    [TestCase(0x6AUL, SpecialKey.F16)]
    public void FunctionKeys_AreMapped(ulong vk, SpecialKey expected)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(MacSpecialKeyMap.Instance.TryGet(vk, out var key), Is.True);
            Assert.That(key, Is.EqualTo(expected));
        }
    }

    [TestCase(0x7BUL, SpecialKey.Left)]
    [TestCase(0x7CUL, SpecialKey.Right)]
    [TestCase(0x7EUL, SpecialKey.Up)]
    [TestCase(0x7DUL, SpecialKey.Down)]
    [TestCase(0x73UL, SpecialKey.Home)]
    [TestCase(0x77UL, SpecialKey.End)]
    [TestCase(0x74UL, SpecialKey.PageUp)]
    [TestCase(0x79UL, SpecialKey.PageDown)]
    public void NavigationKeys_AreMapped(ulong vk, SpecialKey expected)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(MacSpecialKeyMap.Instance.TryGet(vk, out var key), Is.True);
            Assert.That(key, Is.EqualTo(expected));
        }
    }

    [TestCase(0x38UL, SpecialKey.Shift_L)]
    [TestCase(0x3CUL, SpecialKey.Shift_R)]
    [TestCase(0x3BUL, SpecialKey.Control_L)]
    [TestCase(0x3EUL, SpecialKey.Control_R)]
    [TestCase(0x3AUL, SpecialKey.Alt_L)]
    [TestCase(0x3DUL, SpecialKey.Alt_R)]
    [TestCase(0x37UL, SpecialKey.Super_L)]
    [TestCase(0x36UL, SpecialKey.Super_R)]
    [TestCase(0x39UL, SpecialKey.CapsLock)]
    public void ModifierKeys_AreMapped(ulong vk, SpecialKey expected)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(MacSpecialKeyMap.Instance.TryGet(vk, out var key), Is.True);
            Assert.That(key, Is.EqualTo(expected));
        }
    }

    [TestCase(0x52UL, SpecialKey.KP_0)]
    [TestCase(0x5CUL, SpecialKey.KP_9)]
    [TestCase(0x4CUL, SpecialKey.KP_Enter)]
    [TestCase(0x47UL, SpecialKey.NumLock)]  // keypad clear = numlock on mac
    public void KeypadKeys_AreMapped(ulong vk, SpecialKey expected)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(MacSpecialKeyMap.Instance.TryGet(vk, out var key), Is.True);
            Assert.That(key, Is.EqualTo(expected));
        }
    }

    [Test]
    public void UnknownVirtualKey_ReturnsFalse()
    {
        // 0x00 (kVK_ANSI_A) is a character key, not in the special map
        Assert.That(MacSpecialKeyMap.Instance.TryGet(0x00, out _), Is.False);
    }
}
