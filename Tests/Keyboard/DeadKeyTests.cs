using Hydra.Keyboard;

namespace Tests.Keyboard;

[TestFixture]
public class DeadKeyTests
{
    // -- SpacingToCombining table --

    [Test]
    public void SpacingToCombining_HasExpectedEntries()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(KeyResolver.SpacingToCombining['\u0060'], Is.EqualTo('\u0300'));  // ` → combining grave
            Assert.That(KeyResolver.SpacingToCombining['\u00B4'], Is.EqualTo('\u0301'));  // ´ → combining acute
            Assert.That(KeyResolver.SpacingToCombining['\u005E'], Is.EqualTo('\u0302'));  // ^ → combining circumflex
            Assert.That(KeyResolver.SpacingToCombining['\u007E'], Is.EqualTo('\u0303'));  // ~ → combining tilde
            Assert.That(KeyResolver.SpacingToCombining['\u00AF'], Is.EqualTo('\u0304'));  // ¯ → combining macron
            Assert.That(KeyResolver.SpacingToCombining['\u02D8'], Is.EqualTo('\u0306'));  // ˘ → combining breve
            Assert.That(KeyResolver.SpacingToCombining['\u02D9'], Is.EqualTo('\u0307'));  // ˙ → combining dot above
            Assert.That(KeyResolver.SpacingToCombining['\u00A8'], Is.EqualTo('\u0308'));  // ¨ → combining diaeresis
            Assert.That(KeyResolver.SpacingToCombining['\u02DA'], Is.EqualTo('\u030A'));  // ˚ → combining ring above
            Assert.That(KeyResolver.SpacingToCombining['\u02DD'], Is.EqualTo('\u030B'));  // ˝ → combining double acute
            Assert.That(KeyResolver.SpacingToCombining['\u02C7'], Is.EqualTo('\u030C'));  // ˇ → combining caron
            Assert.That(KeyResolver.SpacingToCombining['\u00B8'], Is.EqualTo('\u0327'));  // ¸ → combining cedilla
            Assert.That(KeyResolver.SpacingToCombining['\u02DB'], Is.EqualTo('\u0328'));  // ˛ → combining ogonek
            Assert.That(KeyResolver.SpacingToCombining['\u002F'], Is.EqualTo('\u0335'));  // / → combining stroke
            Assert.That(KeyResolver.SpacingToCombining, Has.Count.EqualTo(14));
        }
    }

    // -- ComposeOrSpacing --

    [Test]
    public void ComposeOrSpacing_SpaceWithSpacing_ReturnsSpacingForm()
    {
        // dead_circumflex + space → ^
        var result = KeyResolver.ComposeOrSpacing(' ', '\u0302', '\u005E');
        Assert.That(result, Is.EqualTo('^'));
    }

    [Test]
    public void ComposeOrSpacing_SpaceWithNoSpacing_ReturnsSpace()
    {
        // no spacing form available → space passes through
        var result = KeyResolver.ComposeOrSpacing(' ', '\u0302', '\0');
        Assert.That(result, Is.EqualTo(' '));
    }

    [Test]
    public void ComposeOrSpacing_CompatiblePair_ReturnsComposed()
    {
        // dead acute + e → é
        var result = KeyResolver.ComposeOrSpacing('e', '\u0301', '\u00B4');
        Assert.That(result, Is.EqualTo('é'));
    }

    [Test]
    public void ComposeOrSpacing_IncompatiblePair_ReturnsBase()
    {
        // dead circumflex + x → x (no precomposed form exists)
        var result = KeyResolver.ComposeOrSpacing('x', '\u0302', '\u005E');
        Assert.That(result, Is.EqualTo('x'));
    }

    // -- Vietnamese dead keys --

    [Test]
    public void ComposeOrSpacing_DeadHook_ComposesVietnamese()
    {
        // dead_hook + a → ả (a with hook above)
        var result = KeyResolver.ComposeOrSpacing('a', '\u0309', '\0');
        Assert.That(result, Is.EqualTo('\u1EA3'));
    }

    [Test]
    public void ComposeOrSpacing_DeadBelowDot_ComposesVietnamese()
    {
        // dead_belowdot + e → ẹ (e with dot below)
        var result = KeyResolver.ComposeOrSpacing('e', '\u0323', '\0');
        Assert.That(result, Is.EqualTo('\u1EB9'));
    }

    [Test]
    public void ComposeOrSpacing_DeadHorn_ComposesVietnamese()
    {
        // dead_horn + u → ư (u with horn)
        var result = KeyResolver.ComposeOrSpacing('u', '\u031B', '\0');
        Assert.That(result, Is.EqualTo('\u01B0'));
    }

    // -- Romanian dead key --

    [Test]
    public void ComposeOrSpacing_DeadBelowComma_ComposesRomanian()
    {
        // dead_belowcomma + s → ș (s with comma below)
        var result = KeyResolver.ComposeOrSpacing('s', '\u0326', '\0');
        Assert.That(result, Is.EqualTo('\u0219'));
    }

    // -- Polytonic Greek dead keys --

    [Test]
    public void ComposeOrSpacing_DeadPsili_ComposesGreek()
    {
        // dead_abovecomma/psili + α → ἀ (alpha with smooth breathing)
        var result = KeyResolver.ComposeOrSpacing('\u03B1', '\u0313', '\u1FBD');
        Assert.That(result, Is.EqualTo('\u1F00'));
    }

    [Test]
    public void ComposeOrSpacing_DeadDasia_ComposesGreek()
    {
        // dead_abovereversedcomma/dasia + α → ἁ (alpha with rough breathing)
        var result = KeyResolver.ComposeOrSpacing('\u03B1', '\u0314', '\u1FFE');
        Assert.That(result, Is.EqualTo('\u1F01'));
    }

    [Test]
    public void ComposeOrSpacing_DeadIota_ComposesGreek()
    {
        // dead_iota + α → ᾳ (alpha with ypogegrammeni)
        var result = KeyResolver.ComposeOrSpacing('\u03B1', '\u0345', '\u037A');
        Assert.That(result, Is.EqualTo('\u1FB3'));
    }

    [Test]
    public void ComposeOrSpacing_DeadKeyWithNoSpacingForm_SpaceReturnsSpace()
    {
        // dead_hook (no spacing form) + space → space passes through
        var result = KeyResolver.ComposeOrSpacing(' ', '\u0309', '\0');
        Assert.That(result, Is.EqualTo(' '));
    }
}
