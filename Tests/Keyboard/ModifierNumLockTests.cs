using Hydra.Keyboard;

namespace Tests.Keyboard;

[TestFixture]
public class ModifierNumLockTests
{
    private const int Kp4 = 75;   // the evdev code; any stamped physical key will do

    private static KeyEvent Key(KeyEventType type, SpecialKey key, KeyModifiers mods, bool repeat = false, int physical = Kp4) =>
        KeyEvent.Special(type, key, mods) with { IsRepeat = repeat, PhysicalKey = physical };

    [TestCase(KeyModifiers.Super)]
    [TestCase(KeyModifiers.Control)]
    [TestCase(KeyModifiers.Alt)]
    public void True_UnderAModifier_TheNavigationKeyCrossesAsItsDigit(KeyModifiers modifier)
    {
        var sent = new ModifierNumLock(true).Apply(Key(KeyEventType.KeyDown, SpecialKey.KP_Left, modifier));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sent.Key, Is.EqualTo(SpecialKey.KP_4));
            Assert.That(sent.Modifiers, Is.EqualTo(modifier | KeyModifiers.NumLock));
        }
    }

    [Test]
    public void False_UnderAModifier_TheDigitCrossesAsItsNavigationKey()
    {
        var sent = new ModifierNumLock(false).Apply(Key(KeyEventType.KeyDown, SpecialKey.KP_4, KeyModifiers.Super | KeyModifiers.NumLock));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sent.Key, Is.EqualTo(SpecialKey.KP_Left));
            Assert.That(sent.Modifiers, Is.EqualTo(KeyModifiers.Super));
        }
    }

    [Test]
    public void Null_LeavesEveryKeyAlone()
    {
        var down = Key(KeyEventType.KeyDown, SpecialKey.KP_Left, KeyModifiers.Super);
        Assert.That(new ModifierNumLock(null).Apply(down), Is.SameAs(down));
    }

    [TestCase(KeyModifiers.None)]
    [TestCase(KeyModifiers.Shift)]
    [TestCase(KeyModifiers.AltGr)]
    public void WithoutSuperControlOrAlt_TheKeyIsLeftAlone(KeyModifiers mods)
    {
        var down = Key(KeyEventType.KeyDown, SpecialKey.KP_Left, mods);
        Assert.That(new ModifierNumLock(true).Apply(down), Is.SameAs(down));
    }

    [TestCase(SpecialKey.Left)]
    [TestCase(SpecialKey.KP_Enter)]
    [TestCase(SpecialKey.KP_Add)]
    public void KeysNumLockDoesNotChange_AreLeftAlone(SpecialKey key)
    {
        var down = Key(KeyEventType.KeyDown, key, KeyModifiers.Super);
        Assert.That(new ModifierNumLock(true).Apply(down), Is.SameAs(down));
    }

    [Test]
    public void TheKey_KeepsItsNumLock_ThroughRepeatsAndUp_AfterTheModifierIsLetGo()
    {
        var numLock = new ModifierNumLock(true);
        KeyEvent[] sent =
        [
            numLock.Apply(Key(KeyEventType.KeyDown, SpecialKey.KP_Left, KeyModifiers.Super)),
            numLock.Apply(Key(KeyEventType.KeyDown, SpecialKey.KP_Left, KeyModifiers.None, repeat: true)),
            numLock.Apply(Key(KeyEventType.KeyUp, SpecialKey.KP_Left, KeyModifiers.None)),
        ];
        Assert.That(sent.Select(e => e.Key), Is.EqualTo([SpecialKey.KP_4, SpecialKey.KP_4, SpecialKey.KP_4]));
    }

    [Test]
    public void AfterItsUp_ThePlainKeyIsItselfAgain()
    {
        var numLock = new ModifierNumLock(true);
        numLock.Apply(Key(KeyEventType.KeyDown, SpecialKey.KP_Left, KeyModifiers.Super));
        numLock.Apply(Key(KeyEventType.KeyUp, SpecialKey.KP_Left, KeyModifiers.Super));
        var plain = Key(KeyEventType.KeyDown, SpecialKey.KP_Left, KeyModifiers.None);
        Assert.That(numLock.Apply(plain), Is.SameAs(plain));
    }

    [Test]
    public void AKeyPressedPlain_StaysPlain_WhenAModifierComesDuringIt()
    {
        var numLock = new ModifierNumLock(true);
        numLock.Apply(Key(KeyEventType.KeyDown, SpecialKey.KP_Left, KeyModifiers.None));
        var repeat = Key(KeyEventType.KeyDown, SpecialKey.KP_Left, KeyModifiers.Super, repeat: true);
        var up = Key(KeyEventType.KeyUp, SpecialKey.KP_Left, KeyModifiers.Super);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(numLock.Apply(repeat), Is.SameAs(repeat));
            Assert.That(numLock.Apply(up), Is.SameAs(up));
        }
    }

    private static KeyEvent Decimal(KeyEventType type, char ch, KeyModifiers mods, bool keypad = true) =>
        KeyEvent.Char(type, ch, mods) with { PhysicalKey = 83, IsKeypadDecimal = keypad };

    [TestCase('.')]
    [TestCase(',')]
    public void False_TheKeypadDecimalsCharacter_CrossesAsKpDelete_DownToUp(char ch)
    {
        var numLock = new ModifierNumLock(false);
        KeyEvent[] sent =
        [
            numLock.Apply(Decimal(KeyEventType.KeyDown, ch, KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.NumLock)),
            numLock.Apply(Decimal(KeyEventType.KeyUp, ch, KeyModifiers.NumLock)),
        ];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sent.Select(e => (e.Key, e.Character)), Is.EqualTo([((SpecialKey?)SpecialKey.KP_Delete, (char?)null), (SpecialKey.KP_Delete, null)]));
            Assert.That(sent[0].Modifiers, Is.EqualTo(KeyModifiers.Control | KeyModifiers.Alt));
        }
    }

    [Test]
    public void True_TheKeypadDecimalsCharacter_StaysItsCharacter_WithNumLockSet()
    {
        var sent = new ModifierNumLock(true).Apply(Decimal(KeyEventType.KeyDown, '.', KeyModifiers.Super));
        using (Assert.EnterMultipleScope())
        {
            Assert.That((sent.Key, sent.Character), Is.EqualTo(((SpecialKey?)null, (char?)'.')));
            Assert.That(sent.Modifiers, Is.EqualTo(KeyModifiers.Super | KeyModifiers.NumLock));
        }
    }

    [Test]
    public void True_KpDelete_CrossesAsKpDecimal()
    {
        var sent = new ModifierNumLock(true).Apply(Key(KeyEventType.KeyDown, SpecialKey.KP_Delete, KeyModifiers.Super, physical: 83));
        Assert.That(sent.Key, Is.EqualTo(SpecialKey.KP_Decimal));
    }

    [Test]
    public void False_AKpDecimalFromAKeypadComma_IsLeftAlone()
    {
        // X11's KP_Decimal-based keypad comma: KP_Decimal with NumLock off too, so it has no navigation form to give
        var comma = Key(KeyEventType.KeyDown, SpecialKey.KP_Decimal, KeyModifiers.Control, physical: 129);
        Assert.That(new ModifierNumLock(false).Apply(comma), Is.SameAs(comma));
    }

    [Test]
    public void False_KpDecimalFromTheDecimalKey_IsKpDelete()
    {
        // a macOS master's decimal repeats as KP_Decimal
        var repeat = Key(KeyEventType.KeyDown, SpecialKey.KP_Decimal, KeyModifiers.Super, physical: 65) with { IsKeypadDecimal = true };
        Assert.That(new ModifierNumLock(false).Apply(repeat).Key, Is.EqualTo(SpecialKey.KP_Delete));
    }

    [Test]
    public void TheMainRowsCharacter_IsLeftAlone()
    {
        var dot = Decimal(KeyEventType.KeyDown, '.', KeyModifiers.Control, keypad: false);
        Assert.That(new ModifierNumLock(false).Apply(dot), Is.SameAs(dot));
    }

    [Test]
    public void AnUnstampedKey_IsLeftAlone()
    {
        var down = Key(KeyEventType.KeyDown, SpecialKey.KP_Left, KeyModifiers.Super, physical: KeyEvent.Unstamped);
        Assert.That(new ModifierNumLock(true).Apply(down), Is.SameAs(down));
    }

    [Test]
    public void WithNumLock_PairsEveryKeypadDigitWithItsNavigationKey()
    {
        SpecialKey[] digits = [SpecialKey.KP_0, SpecialKey.KP_1, SpecialKey.KP_2, SpecialKey.KP_3, SpecialKey.KP_4, SpecialKey.KP_5,
            SpecialKey.KP_6, SpecialKey.KP_7, SpecialKey.KP_8, SpecialKey.KP_9, SpecialKey.KP_Decimal];
        using (Assert.EnterMultipleScope())
        {
            foreach (var digit in digits)
            {
                var navigation = digit.WithNumLock(false);
                Assert.That(navigation, Is.Not.EqualTo(digit), $"{digit} has a navigation key");
                Assert.That(navigation.IsNumLockSensitive(), Is.True, $"{navigation} is a keypad key");
                Assert.That(navigation.WithNumLock(true), Is.EqualTo(digit), $"{navigation} goes back to {digit}");
                Assert.That(digit.WithNumLock(true), Is.EqualTo(digit), $"{digit} with NumLock on is itself");
            }
        }
    }
}
