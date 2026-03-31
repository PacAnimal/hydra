using Hydra.Keyboard;
using Hydra.Platform.Windows;

namespace Tests.Keyboard;

[TestFixture]
public class WinSpecialKeyMapTests
{
    [TestCase(0x70, SpecialKey.F1)]
    [TestCase(0x71, SpecialKey.F2)]
    [TestCase(0x74, SpecialKey.F5)]
    [TestCase(0x79, SpecialKey.F10)]
    [TestCase(0x7B, SpecialKey.F12)]
    [TestCase(0x7C, SpecialKey.F13)]
    [TestCase(0x7F, SpecialKey.F16)]
    public void FunctionKeys_AreMapped(int vk, SpecialKey expected)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(WinSpecialKeyMap.TryGet(vk, out var key), Is.True);
            Assert.That(key, Is.EqualTo(expected));
        }
    }

    [TestCase(0x25, SpecialKey.Left)]
    [TestCase(0x27, SpecialKey.Right)]
    [TestCase(0x26, SpecialKey.Up)]
    [TestCase(0x28, SpecialKey.Down)]
    [TestCase(0x24, SpecialKey.Home)]
    [TestCase(0x23, SpecialKey.End)]
    [TestCase(0x21, SpecialKey.PageUp)]
    [TestCase(0x22, SpecialKey.PageDown)]
    [TestCase(0x2D, SpecialKey.Insert)]
    [TestCase(0x2E, SpecialKey.Delete)]
    public void NavigationKeys_AreMapped(int vk, SpecialKey expected)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(WinSpecialKeyMap.TryGet(vk, out var key), Is.True);
            Assert.That(key, Is.EqualTo(expected));
        }
    }

    [TestCase(0xA0, SpecialKey.Shift_L)]
    [TestCase(0xA1, SpecialKey.Shift_R)]
    [TestCase(0xA2, SpecialKey.Control_L)]
    [TestCase(0xA3, SpecialKey.Control_R)]
    [TestCase(0xA4, SpecialKey.Alt_L)]
    [TestCase(0xA5, SpecialKey.Alt_R)]
    [TestCase(0x5B, SpecialKey.Super_L)]
    [TestCase(0x5C, SpecialKey.Super_R)]
    [TestCase(0x14, SpecialKey.CapsLock)]
    [TestCase(0x90, SpecialKey.NumLock)]
    [TestCase(0x91, SpecialKey.ScrollLock)]
    public void ModifierKeys_AreMapped(int vk, SpecialKey expected)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(WinSpecialKeyMap.TryGet(vk, out var key), Is.True);
            Assert.That(key, Is.EqualTo(expected));
        }
    }

    [TestCase(0x60, SpecialKey.KP_0)]
    [TestCase(0x69, SpecialKey.KP_9)]
    [TestCase(0x6A, SpecialKey.KP_Multiply)]
    public void KeypadKeys_AreMapped(int vk, SpecialKey expected)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(WinSpecialKeyMap.TryGet(vk, out var key), Is.True);
            Assert.That(key, Is.EqualTo(expected));
        }
    }

    [TestCase(0xAD, SpecialKey.AudioMute)]
    [TestCase(0xAE, SpecialKey.AudioVolumeDown)]
    [TestCase(0xAF, SpecialKey.AudioVolumeUp)]
    [TestCase(0xB0, SpecialKey.AudioNext)]
    [TestCase(0xB1, SpecialKey.AudioPrev)]
    [TestCase(0xB2, SpecialKey.AudioStop)]
    [TestCase(0xB3, SpecialKey.AudioPlay)]
    public void MediaKeys_AreMapped(int vk, SpecialKey expected)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(WinSpecialKeyMap.TryGet(vk, out var key), Is.True);
            Assert.That(key, Is.EqualTo(expected));
        }
    }

    [Test]
    public void UnknownVirtualKey_ReturnsFalse()
    {
        // 0x41 (VK_A) is a character key, not in the special map
        Assert.That(WinSpecialKeyMap.TryGet(0x41, out _), Is.False);
    }
}
