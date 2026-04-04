using Hydra.Keyboard;
using Hydra.Platform.Windows;

namespace Tests.Keyboard;

[TestFixture]
public class WinSpecialKeyMapTests
{
    [TestCase(0x70UL, SpecialKey.F1)]
    [TestCase(0x71UL, SpecialKey.F2)]
    [TestCase(0x74UL, SpecialKey.F5)]
    [TestCase(0x79UL, SpecialKey.F10)]
    [TestCase(0x7BUL, SpecialKey.F12)]
    [TestCase(0x7CUL, SpecialKey.F13)]
    [TestCase(0x7FUL, SpecialKey.F16)]
    public void FunctionKeys_AreMapped(ulong vk, SpecialKey expected)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(WinSpecialKeyMap.Instance.TryGet(vk, out var key), Is.True);
            Assert.That(key, Is.EqualTo(expected));
        }
    }

    [TestCase(0x25UL, SpecialKey.Left)]
    [TestCase(0x27UL, SpecialKey.Right)]
    [TestCase(0x26UL, SpecialKey.Up)]
    [TestCase(0x28UL, SpecialKey.Down)]
    [TestCase(0x24UL, SpecialKey.Home)]
    [TestCase(0x23UL, SpecialKey.End)]
    [TestCase(0x21UL, SpecialKey.PageUp)]
    [TestCase(0x22UL, SpecialKey.PageDown)]
    [TestCase(0x2DUL, SpecialKey.Insert)]
    [TestCase(0x2EUL, SpecialKey.Delete)]
    public void NavigationKeys_AreMapped(ulong vk, SpecialKey expected)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(WinSpecialKeyMap.Instance.TryGet(vk, out var key), Is.True);
            Assert.That(key, Is.EqualTo(expected));
        }
    }

    [TestCase(0xA0UL, SpecialKey.Shift_L)]
    [TestCase(0xA1UL, SpecialKey.Shift_R)]
    [TestCase(0xA2UL, SpecialKey.Control_L)]
    [TestCase(0xA3UL, SpecialKey.Control_R)]
    [TestCase(0xA4UL, SpecialKey.Alt_L)]
    [TestCase(0xA5UL, SpecialKey.Alt_R)]
    [TestCase(0x5BUL, SpecialKey.Super_L)]
    [TestCase(0x5CUL, SpecialKey.Super_R)]
    [TestCase(0x14UL, SpecialKey.CapsLock)]
    [TestCase(0x90UL, SpecialKey.NumLock)]
    [TestCase(0x91UL, SpecialKey.ScrollLock)]
    public void ModifierKeys_AreMapped(ulong vk, SpecialKey expected)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(WinSpecialKeyMap.Instance.TryGet(vk, out var key), Is.True);
            Assert.That(key, Is.EqualTo(expected));
        }
    }

    [TestCase(0x60UL, SpecialKey.KP_0)]
    [TestCase(0x69UL, SpecialKey.KP_9)]
    [TestCase(0x6AUL, SpecialKey.KP_Multiply)]
    public void KeypadKeys_AreMapped(ulong vk, SpecialKey expected)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(WinSpecialKeyMap.Instance.TryGet(vk, out var key), Is.True);
            Assert.That(key, Is.EqualTo(expected));
        }
    }

    [TestCase(0xADUL, SpecialKey.AudioMute)]
    [TestCase(0xAEUL, SpecialKey.AudioVolumeDown)]
    [TestCase(0xAFUL, SpecialKey.AudioVolumeUp)]
    [TestCase(0xB0UL, SpecialKey.AudioNext)]
    [TestCase(0xB1UL, SpecialKey.AudioPrev)]
    [TestCase(0xB2UL, SpecialKey.AudioStop)]
    [TestCase(0xB3UL, SpecialKey.AudioPlay)]
    public void MediaKeys_AreMapped(ulong vk, SpecialKey expected)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(WinSpecialKeyMap.Instance.TryGet(vk, out var key), Is.True);
            Assert.That(key, Is.EqualTo(expected));
        }
    }

    [Test]
    public void UnknownVirtualKey_ReturnsFalse()
    {
        // 0x41 (VK_A) is a character key, not in the special map
        Assert.That(WinSpecialKeyMap.Instance.TryGet(0x41, out _), Is.False);
    }

    [Test]
    public void NoDuplicateSpecialKeys()
    {
        // Reverse construction throws if two VK codes map to the same SpecialKey
        Assert.That(WinSpecialKeyMap.Instance.Reverse, Is.Not.Empty);
    }
}
