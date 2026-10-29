using Hydra.Keyboard;
using Hydra.Platform.Linux;
using Hydra.Platform.Windows;

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
            Assert.That((uint)SpecialKey.Pause, Is.EqualTo(0x01FF13u));        // XK_Pause
            Assert.That((uint)SpecialKey.PrintScreen, Is.EqualTo(0x01FF61u));  // XK_Print
            Assert.That((uint)SpecialKey.Menu, Is.EqualTo(0x01FF67u));         // XK_Menu
        }
    }

    // Pause/PrintScreen were added later than the rest, so pin that both platform maps carry them in
    // both directions -- the reverse map is what the slave injects from.
    [TestCase(SpecialKey.Pause)]
    [TestCase(SpecialKey.PrintScreen)]
    [TestCase(SpecialKey.Menu)]
    [TestCase(SpecialKey.ScrollLock)]
    public void PcExtraKeys_RoundTripThroughWindowsAndX11Maps(SpecialKey key)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(WinSpecialKeyMap.Instance.Reverse.TryGetValue(key, out var vk), Is.True, $"no Windows VK for {key}");
            Assert.That(WinSpecialKeyMap.Instance.TryGet(vk, out var backFromVk) ? backFromVk : default, Is.EqualTo(key));

            Assert.That(XorgSpecialKeyMap.Instance.Reverse.TryGetValue(key, out var keysym), Is.True, $"no X11 keysym for {key}");
            Assert.That(XorgSpecialKeyMap.Instance.TryGet(keysym, out var backFromKeysym) ? backFromKeysym : default, Is.EqualTo(key));

            // the enum encodes the X11 keysym, so the two must agree
            Assert.That(keysym, Is.EqualTo((ulong)key & 0xFFFF), $"{key} disagrees with its own keysym encoding");
        }
    }

    [TestCase(SpecialKey.KP_Home, SpecialKey.Home)]
    [TestCase(SpecialKey.KP_Left, SpecialKey.Left)]
    [TestCase(SpecialKey.KP_Up, SpecialKey.Up)]
    [TestCase(SpecialKey.KP_Right, SpecialKey.Right)]
    [TestCase(SpecialKey.KP_Down, SpecialKey.Down)]
    [TestCase(SpecialKey.KP_PageUp, SpecialKey.PageUp)]
    [TestCase(SpecialKey.KP_PageDown, SpecialKey.PageDown)]
    [TestCase(SpecialKey.KP_End, SpecialKey.End)]
    [TestCase(SpecialKey.KP_Insert, SpecialKey.Insert)]
    [TestCase(SpecialKey.KP_Delete, SpecialKey.Delete)]
    public void KeypadNavigation_PairsWithItsDedicatedKey(SpecialKey keypad, SpecialKey dedicated)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(keypad.WithoutKeypad(), Is.EqualTo(dedicated));
            Assert.That(dedicated.KeypadTwin(), Is.EqualTo(keypad));
            Assert.That(XorgSpecialKeyMap.Instance.Reverse[keypad], Is.EqualTo((ulong)keypad & 0xFFFF), "the X11 slave injects its own keysym");
        }
    }

    [Test]
    public void KeypadBegin_HasNoTwin_AndRoundTripsThroughWindowsAndX11Maps()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(SpecialKey.KP_Begin.WithoutKeypad(), Is.EqualTo(SpecialKey.KP_Begin));
            Assert.That(WinSpecialKeyMap.Instance.Reverse[SpecialKey.KP_Begin], Is.EqualTo((ulong)WinVirtualKey.Clear));
            Assert.That(XorgSpecialKeyMap.Instance.Reverse[SpecialKey.KP_Begin], Is.EqualTo(0xFF9DUL));
        }
    }

    [TestCase(SpecialKey.F1)]
    [TestCase(SpecialKey.KP_4)]
    [TestCase(SpecialKey.Return)]
    public void NotNavigation_HasNoTwin(SpecialKey key)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(key.WithoutKeypad(), Is.EqualTo(key));
            Assert.That(key.KeypadTwin(), Is.Null);
        }
    }

    [TestCase(SpecialKey.KP_0)]
    [TestCase(SpecialKey.KP_9)]
    [TestCase(SpecialKey.KP_Decimal)]
    [TestCase(SpecialKey.KP_Left)]
    [TestCase(SpecialKey.KP_Begin)]
    [TestCase(SpecialKey.KP_Delete)]
    public void KeypadKeysNumLockChanges_AreNumLockSensitive(SpecialKey key) =>
        Assert.That(key.IsNumLockSensitive(), Is.True);

    [TestCase(SpecialKey.Left)]
    [TestCase(SpecialKey.KP_Enter)]
    [TestCase(SpecialKey.KP_Add)]
    [TestCase(SpecialKey.F1)]
    public void KeysNumLockLeavesAlone_AreNotNumLockSensitive(SpecialKey key) =>
        Assert.That(key.IsNumLockSensitive(), Is.False);

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
