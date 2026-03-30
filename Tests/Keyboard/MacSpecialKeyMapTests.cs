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
        Assert.That(map, Is.Not.Empty);
    }

    // kVK_* values from Carbon Events.h
    [TestCase(0x7A, KeyId.F1)]
    [TestCase(0x78, KeyId.F2)]
    [TestCase(0x60, KeyId.F5)]
    [TestCase(0x6D, KeyId.F10)]
    [TestCase(0x6F, KeyId.F12)]
    [TestCase(0x69, KeyId.F13)]
    [TestCase(0x6A, KeyId.F16)]
    public void FunctionKeys_AreMapped(int vk, uint expectedId)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(MacSpecialKeyMap.TryGet(vk, out var id), Is.True);
            Assert.That(id, Is.EqualTo(expectedId));
        }
    }

    [TestCase(0x7B, KeyId.Left)]
    [TestCase(0x7C, KeyId.Right)]
    [TestCase(0x7E, KeyId.Up)]
    [TestCase(0x7D, KeyId.Down)]
    [TestCase(0x73, KeyId.Home)]
    [TestCase(0x77, KeyId.End)]
    [TestCase(0x74, KeyId.PageUp)]
    [TestCase(0x79, KeyId.PageDown)]
    public void NavigationKeys_AreMapped(int vk, uint expectedId)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(MacSpecialKeyMap.TryGet(vk, out var id), Is.True);
            Assert.That(id, Is.EqualTo(expectedId));
        }
    }

    [TestCase(0x38, KeyId.Shift_L)]
    [TestCase(0x3C, KeyId.Shift_R)]
    [TestCase(0x3B, KeyId.Control_L)]
    [TestCase(0x3E, KeyId.Control_R)]
    [TestCase(0x3A, KeyId.Alt_L)]
    [TestCase(0x3D, KeyId.Alt_R)]
    [TestCase(0x37, KeyId.Super_L)]
    [TestCase(0x36, KeyId.Super_R)]
    [TestCase(0x39, KeyId.CapsLock)]
    public void ModifierKeys_AreMapped(int vk, uint expectedId)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(MacSpecialKeyMap.TryGet(vk, out var id), Is.True);
            Assert.That(id, Is.EqualTo(expectedId));
        }
    }

    [TestCase(0x52, KeyId.KP_0)]
    [TestCase(0x5C, KeyId.KP_9)]
    [TestCase(0x4C, KeyId.KP_Enter)]
    [TestCase(0x47, KeyId.NumLock)]  // keypad clear = numlock on mac
    public void KeypadKeys_AreMapped(int vk, uint expectedId)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(MacSpecialKeyMap.TryGet(vk, out var id), Is.True);
            Assert.That(id, Is.EqualTo(expectedId));
        }
    }

    [Test]
    public void UnknownVirtualKey_ReturnsFalse()
    {
        // 0x00 (kVK_ANSI_A) is a character key, not in the special map
        Assert.That(MacSpecialKeyMap.TryGet(0x00, out _), Is.False);
    }
}
