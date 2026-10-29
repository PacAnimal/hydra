using Hydra.Keyboard;
using Hydra.Platform.Linux;

namespace Tests.Keyboard;

[TestFixture]
public class ShiftedKeypadTests
{
    [Test]
    public void KeypadNavigation_WithShift_IsTheDedicatedKey() =>
        Assert.That(new ShiftedKeypad().Injected(SpecialKey.KP_Left, KeyEventType.KeyDown, KeyModifiers.Shift), Is.EqualTo(SpecialKey.Left));

    [TestCase(SpecialKey.KP_Left, KeyModifiers.None)]
    [TestCase(SpecialKey.KP_4, KeyModifiers.Shift)]
    [TestCase(SpecialKey.KP_Begin, KeyModifiers.Shift)]
    public void OtherwiseTheKeyItself(SpecialKey key, KeyModifiers mods) =>
        Assert.That(new ShiftedKeypad().Injected(key, KeyEventType.KeyDown, mods), Is.EqualTo(key));

    [Test]
    public void RepeatsAndUp_FollowThePress_AfterShiftIsReleased()
    {
        var keypad = new ShiftedKeypad();
        keypad.Injected(SpecialKey.KP_Left, KeyEventType.KeyDown, KeyModifiers.Shift);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(keypad.Injected(SpecialKey.KP_Left, KeyEventType.KeyDown, KeyModifiers.None), Is.EqualTo(SpecialKey.Left));
            Assert.That(keypad.Injected(SpecialKey.KP_Left, KeyEventType.KeyUp, KeyModifiers.None), Is.EqualTo(SpecialKey.Left));
            Assert.That(keypad.Injected(SpecialKey.KP_Left, KeyEventType.KeyDown, KeyModifiers.None), Is.EqualTo(SpecialKey.KP_Left), "the next press decides afresh");
        }
    }

    [Test]
    public void PressedWithoutShift_StaysTheKeypadKey_WhenShiftArrives()
    {
        var keypad = new ShiftedKeypad();
        keypad.Injected(SpecialKey.KP_Left, KeyEventType.KeyDown, KeyModifiers.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(keypad.Injected(SpecialKey.KP_Left, KeyEventType.KeyDown, KeyModifiers.Shift), Is.EqualTo(SpecialKey.KP_Left), "a repeat");
            Assert.That(keypad.Injected(SpecialKey.KP_Left, KeyEventType.KeyUp, KeyModifiers.Shift), Is.EqualTo(SpecialKey.KP_Left));
        }
    }
}
