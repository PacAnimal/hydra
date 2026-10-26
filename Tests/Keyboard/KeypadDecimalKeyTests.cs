using Hydra.Keyboard;

namespace Tests.Keyboard;

// the key each master stamps as the keypad's decimal: the one whose NumLock-off form there is KP_Delete
[TestFixture]
public class KeypadDecimalKeyTests
{
    [TestCase(83u, true)]     // KEY_KPDOT
    [TestCase(121u, true)]    // KEY_KPCOMMA: Hydra's evdev NumLock-off sends it as KP_Delete
    [TestCase(52u, false)]    // KEY_DOT, the main row
    [TestCase(111u, false)]   // KEY_DELETE
    public void Evdev(uint code, bool expected) => Assert.That(KeypadDecimalKey.Evdev(code), Is.EqualTo(expected));

    [TestCase(0xFF9FUL, true)]    // KP_Delete
    [TestCase(0xFFAEUL, false)]   // KP_Decimal: an extra keypad comma, which NumLock leaves alone
    [TestCase(0xFFACUL, false)]   // KP_Separator, likewise
    [TestCase(0xFFFFUL, false)]   // Delete
    [TestCase(0x2EUL, false)]     // period
    public void Xorg(ulong baseKeysym, bool expected) => Assert.That(KeypadDecimalKey.Xorg(baseKeysym), Is.EqualTo(expected));

    [TestCase(0x6E, true)]                // VK_DECIMAL
    [TestCase(0x6C, false)]               // VK_SEPARATOR, NumLock-independent
    [TestCase(0xC2, false)]               // VK_ABNT_C2, likewise
    [TestCase(0xBE, false)]               // VK_OEM_PERIOD
    [TestCase(0x2E | 0x10000, false)]     // the decimal with NumLock off, tracked as Delete with the keypad bit
    public void Windows(int tracking, bool expected) => Assert.That(KeypadDecimalKey.Windows(tracking), Is.EqualTo(expected));

    [TestCase(0x41, true)]    // kVK_ANSI_KeypadDecimal
    [TestCase(0x5F, false)]   // kVK_JIS_KeypadComma
    [TestCase(0x2F, false)]   // kVK_ANSI_Period
    public void MacOs(int keycode, bool expected) => Assert.That(KeypadDecimalKey.MacOs(keycode), Is.EqualTo(expected));
}
