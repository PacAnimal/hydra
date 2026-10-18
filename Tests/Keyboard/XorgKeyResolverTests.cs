using Hydra.Keyboard;
using Hydra.Platform.Linux;

namespace Tests.Keyboard;

[TestFixture]
public class XorgKeyResolverTests
{
    // XK_ keysym constants used in tests
    private const ulong XkDeadGrave = 0xFE50;  // Combining=U+0300, Spacing='`'
    private const ulong XkDeadAcute = 0xFE51;  // Combining=U+0301, Spacing='´'
    private const ulong XkDeadBelowdot = 0xFE60; // Combining=U+0323, Spacing='\0' (no spacing form)
    private const ulong XkTab = 0xFF09;  // → SpecialKey.Tab  (non-modifier special)
    private const ulong XkShiftL = 0xFFE1;  // → SpecialKey.Shift_L (modifier — transparent)
    private const ulong XkMetaL = 0xFFE7;   // MISCELLANY, but no SpecialKey
    private const ulong XkA = 0x61;

    // -- KeySymToSpecialKey --

    [Test]
    public void KeySymToSpecialKey_MiscellanyKeysyms_MapOnlyToDefinedKeys()
    {
        var invented = Enumerable.Range(0xFF00, 0x100)
            .Select(k => XorgKeyResolver.KeySymToSpecialKey((ulong)k))
            .Where(k => k.HasValue && !Enum.IsDefined(k.Value));
        Assert.That(invented, Is.Empty);
    }

    [Test]
    public void FlushDeadKeyBeforeSpecial_UnmappedMiscellanyKeysym_KeepsPendingDeadKey()
    {
        var dead = '\u0300';
        var spacing = '`';
        var ev = XorgKeyResolver.FlushDeadKeyBeforeSpecial(XkMetaL, ref dead, ref spacing);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ev, Is.Null);
            Assert.That(dead, Is.EqualTo('\u0300'));
        }
    }

    // -- FlushDeadKeyBeforeSpecial --

    [Test]
    public void FlushDeadKeyBeforeSpecial_PendingDeadAndTabKeysym_FlushesSpacingForm()
    {
        var dead = '\u0300';     // combining grave (from dead_grave)
        var spacing = '\u0060';  // `
        var ev = XorgKeyResolver.FlushDeadKeyBeforeSpecial(XkTab, ref dead, ref spacing);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ev, Is.Not.Null);
            Assert.That(ev![0].Character, Is.EqualTo('`'));
            Assert.That(dead, Is.EqualTo('\0'));
        }
    }

    [Test]
    public void FlushDeadKeyBeforeSpecial_PendingDeadAndModifierKeysym_ReturnsNullModifierTransparent()
    {
        // modifier keys must not abort dead key composition
        var dead = '\u0300';
        var spacing = '`';
        var ev = XorgKeyResolver.FlushDeadKeyBeforeSpecial(XkShiftL, ref dead, ref spacing);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ev, Is.Null);
            Assert.That(dead, Is.EqualTo('\u0300'), "pending dead key must survive modifier press");
        }
    }

    [Test]
    public void FlushDeadKeyBeforeSpecial_NoPendingDead_ReturnsNull()
    {
        var dead = '\0';
        var spacing = '\0';
        var ev = XorgKeyResolver.FlushDeadKeyBeforeSpecial(XkTab, ref dead, ref spacing);
        Assert.That(ev, Is.Null);
    }

    [Test]
    public void FlushDeadKeyBeforeSpecial_PendingDeadNoSpacingFormAndTab_ReturnsNullClearsState()
    {
        // dead_belowdot (no spacing form) + Tab: silently dropped, state cleared
        var dead = '\u0323';
        var spacing = '\0';
        var ev = XorgKeyResolver.FlushDeadKeyBeforeSpecial(XkTab, ref dead, ref spacing);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ev, Is.Null);
            Assert.That(dead, Is.EqualTo('\0'), "state must be cleared even with no spacing form");
        }
    }

    [Test]
    public void FlushDeadKeyBeforeSpecial_PendingDeadAndCharKeysym_ReturnsNull()
    {
        // character keysyms (e.g. 'a' = 0x61) are not special keys — no flush
        var dead = '\u0300';
        var spacing = '`';
        var ev = XorgKeyResolver.FlushDeadKeyBeforeSpecial(0x61UL, ref dead, ref spacing);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ev, Is.Null);
            Assert.That(dead, Is.EqualTo('\u0300'), "character key must not flush pending dead");
        }
    }

    // -- ResolveKeysym: dead + dead sequences --

    [Test]
    public void ResolveKeysym_DeadPlusDead_EmitsSpacingFormOfFirst()
    {
        // dead_grave then dead_acute: spacing form of grave ('`' = U+0060) is emitted
        var dead = '\0';
        var spacing = '\0';
        var keyDownId = new Dictionary<uint, CharClassification>();
        XorgKeyResolver.ResolveKeysym(XkDeadGrave, 1, keyDownId, ref dead, ref spacing, KeyModifiers.None);
        Assert.That(dead, Is.EqualTo('\u0300'));  // grave pending

        var ev = XorgKeyResolver.ResolveKeysym(XkDeadAcute, 2, keyDownId, ref dead, ref spacing, KeyModifiers.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ev, Is.Not.Null);
            Assert.That(ev![0].Character, Is.EqualTo('\u0060'), "spacing form of dead_grave is backtick (U+0060)");
            Assert.That(ev[0].Modifiers, Is.EqualTo(KeyModifiers.None));
            Assert.That(ev[0].Type, Is.EqualTo(KeyEventType.KeyDown));
            Assert.That(ev[1].Type, Is.EqualTo(KeyEventType.KeyUp));
        }
    }

    [Test]
    public void ResolveKeysym_DeadPlusDead_SecondDeadBecomesNewPending()
    {
        var dead = '\0';
        var spacing = '\0';
        var keyDownId = new Dictionary<uint, CharClassification>();
        XorgKeyResolver.ResolveKeysym(XkDeadGrave, 1, keyDownId, ref dead, ref spacing, KeyModifiers.None);

        XorgKeyResolver.ResolveKeysym(XkDeadAcute, 2, keyDownId, ref dead, ref spacing, KeyModifiers.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(dead, Is.EqualTo('\u0301'), "acute combining char must now be pending");
            Assert.That(spacing, Is.EqualTo('\u00B4'), "acute spacing form must be stored");
        }
    }

    [Test]
    public void ResolveKeysym_DeadPlusDead_KeyDownIdPlaceholderSet()
    {
        var dead = '\0';
        var spacing = '\0';
        var keyDownId = new Dictionary<uint, CharClassification>();
        XorgKeyResolver.ResolveKeysym(XkDeadGrave, 1, keyDownId, ref dead, ref spacing, KeyModifiers.None);
        XorgKeyResolver.ResolveKeysym(XkDeadAcute, 2, keyDownId, ref dead, ref spacing, KeyModifiers.None);

        // keycode 2 must have a (null, null) placeholder so key-up doesn't replay the spacing form
        Assert.That(keyDownId.ContainsKey(2), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(keyDownId[2].Ch, Is.Null);
            Assert.That(keyDownId[2].Key, Is.Null);
        }
    }

    [Test]
    public void ResolveKeysym_DeadPlusDeadNoSpacingForm_ReturnsNullAndSilentlyDropsFirst()
    {
        // dead_belowdot has no spacing form; first dead's state is dropped silently
        var dead = '\0';
        var spacing = '\0';
        var keyDownId = new Dictionary<uint, CharClassification>();
        XorgKeyResolver.ResolveKeysym(XkDeadBelowdot, 1, keyDownId, ref dead, ref spacing, KeyModifiers.None);

        var ev = XorgKeyResolver.ResolveKeysym(XkDeadGrave, 2, keyDownId, ref dead, ref spacing, KeyModifiers.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ev, Is.Null, "no spacing form to emit for dead_belowdot");
            Assert.That(dead, Is.EqualTo('\u0300'), "dead_grave combining char must now be pending");
        }
    }


    [TestCase(0xFF95UL, 0xFF50UL)]  // KP_Home → Home
    [TestCase(0xFF96UL, 0xFF51UL)]  // KP_Left → Left
    [TestCase(0xFF97UL, 0xFF52UL)]  // KP_Up → Up
    [TestCase(0xFF98UL, 0xFF53UL)]  // KP_Right → Right
    [TestCase(0xFF99UL, 0xFF54UL)]  // KP_Down → Down
    [TestCase(0xFF9AUL, 0xFF55UL)]  // KP_Prior → PageUp
    [TestCase(0xFF9BUL, 0xFF56UL)]  // KP_Next → PageDown
    [TestCase(0xFF9CUL, 0xFF57UL)]  // KP_End → End
    [TestCase(0xFF9EUL, 0xFF63UL)]  // KP_Insert → Insert
    [TestCase(0xFF9FUL, 0xFFFFUL)]  // KP_Delete → Delete
    public void MapKpNavToStandard_MapsAllNavigationKeys(ulong kpKeysym, ulong expected)
    {
        Assert.That(XorgKeyResolver.MapKpNavToStandard(kpKeysym), Is.EqualTo(expected));
    }

    [Test]
    public void MapKpNavToStandard_KpBegin_PassesThrough()
    {
        // KP_Begin (center 5) has no standard navigation equivalent
        Assert.That(XorgKeyResolver.MapKpNavToStandard(0xFF9DUL), Is.EqualTo(0xFF9DUL));
    }

    [Test]
    public void MapKpNavToStandard_NonKpKey_PassesThrough()
    {
        Assert.That(XorgKeyResolver.MapKpNavToStandard(0xFF51UL), Is.EqualTo(0xFF51UL));
    }

    [TestCase(0xFF9EUL, '0')]  // KP_Insert → 0
    [TestCase(0xFF9CUL, '1')]  // KP_End → 1
    [TestCase(0xFF99UL, '2')]  // KP_Down → 2
    [TestCase(0xFF9BUL, '3')]  // KP_Next → 3
    [TestCase(0xFF96UL, '4')]  // KP_Left → 4
    [TestCase(0xFF9DUL, '5')]  // KP_Begin → 5
    [TestCase(0xFF98UL, '6')]  // KP_Right → 6
    [TestCase(0xFF95UL, '7')]  // KP_Home → 7
    [TestCase(0xFF97UL, '8')]  // KP_Up → 8
    [TestCase(0xFF9AUL, '9')]  // KP_Prior → 9
    [TestCase(0xFF9FUL, (ulong)'.')]  // KP_Delete → .
    public void KpNavToChar_MapsAllPositions(ulong kpKeysym, ulong expectedChar)
    {
        Assert.That(XorgKeyResolver.KpNavToChar(kpKeysym), Is.EqualTo(expectedChar));
    }

    [TestCase(0xFFB0UL, 0xFF9EUL)]  // KP_0 → KP_Insert
    [TestCase(0xFFB1UL, 0xFF9CUL)]  // KP_1 → KP_End
    [TestCase(0xFFB2UL, 0xFF99UL)]  // KP_2 → KP_Down
    [TestCase(0xFFB3UL, 0xFF9BUL)]  // KP_3 → KP_Next
    [TestCase(0xFFB4UL, 0xFF96UL)]  // KP_4 → KP_Left
    [TestCase(0xFFB5UL, 0xFF9DUL)]  // KP_5 → KP_Begin
    [TestCase(0xFFB6UL, 0xFF98UL)]  // KP_6 → KP_Right
    [TestCase(0xFFB7UL, 0xFF95UL)]  // KP_7 → KP_Home
    [TestCase(0xFFB8UL, 0xFF97UL)]  // KP_8 → KP_Up
    [TestCase(0xFFB9UL, 0xFF9AUL)]  // KP_9 → KP_Prior
    [TestCase(0xFFAEUL, 0xFF9FUL)]  // KP_Decimal → KP_Delete
    public void KpNumericToNav_MapsAllDigits(ulong kpNumeric, ulong expectedNav)
    {
        Assert.That(XorgKeyResolver.KpNumericToNav(kpNumeric), Is.EqualTo(expectedNav));
    }

    [Test]
    public void KpNumericToNav_FollowedByMapKpNavToStandard_ProducesStandardNav()
    {
        // round-trip: KP_7 → KP_Home → Home (Left arrow block)
        var nav = XorgKeyResolver.KpNumericToNav(0xFFB7UL);
        var standard = XorgKeyResolver.MapKpNavToStandard(nav);
        Assert.That(standard, Is.EqualTo(0xFF50UL)); // Home
    }

    // -- ResolveDown --

    [Test]
    public void ResolveDown_DeadKeyPendingWithCtrlShortcut_FlushesSpacingFormBeforeShortcut()
    {
        var dead = '\0';
        var spacing = '\0';
        var keyDownId = new Dictionary<uint, CharClassification>();
        XorgKeyResolver.ResolveDown(XkDeadGrave, 1, false, KeyModifiers.None, keyDownId, ref dead, ref spacing);

        var ev = XorgKeyResolver.ResolveDown(XkA, 2, true, KeyModifiers.Control, keyDownId, ref dead, ref spacing);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ev!.Select(e => (e.Type, e.Character, e.Modifiers)), Is.EqualTo(
            [
                (KeyEventType.KeyDown, '`', KeyModifiers.None),
                (KeyEventType.KeyUp, (char?)'`', KeyModifiers.None),
                (KeyEventType.KeyDown, (char?)'a', KeyModifiers.Control),
            ]));
            Assert.That(dead, Is.EqualTo('\0'), "dead key state cleared after flush");
        }
    }

    [Test]
    public void ResolveDown_DeadKeyPendingWithCtrlTab_FlushesOnce()
    {
        var dead = '\u0300';
        var spacing = '`';
        var ev = XorgKeyResolver.ResolveDown(XkTab, 1, true, KeyModifiers.Control, [], ref dead, ref spacing);
        Assert.That(ev!.Select(e => (e.Type, e.Character, e.Key)), Is.EqualTo(
        [
            (KeyEventType.KeyDown, '`', null),
            (KeyEventType.KeyUp, (char?)'`', (SpecialKey?)null),
            (KeyEventType.KeyDown, null, SpecialKey.Tab),
        ]));
    }

    [Test]
    public void ResolveDown_CtrlShortcutWithoutPendingDeadKey_SendsOnlyTheShortcut()
    {
        var dead = '\0';
        var spacing = '\0';
        var ev = XorgKeyResolver.ResolveDown(XkA, 1, true, KeyModifiers.Control, [], ref dead, ref spacing);
        Assert.That(ev!.Select(e => e.Character), Is.EqualTo(new char?[] { 'a' }));
    }

    [Test]
    public void ResolveDown_CtrlOnDeadKey_SendsSpacingForm()
    {
        var dead = '\0';
        var spacing = '\0';
        var ev = XorgKeyResolver.ResolveDown(XkDeadGrave, 1, true, KeyModifiers.Control, [], ref dead, ref spacing);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ev!.Select(e => (e.Character, e.Modifiers)), Is.EqualTo([((char?)'`', KeyModifiers.Control)]));
            Assert.That(dead, Is.EqualTo('\0'), "a shortcut never arms a dead key");
        }
    }

    [Test]
    public void ResolveDown_CtrlOnDeadKeyWithNoSpacingForm_KeepsPendingDeadKey()
    {
        var dead = '\u0300';
        var spacing = '`';
        var ev = XorgKeyResolver.ResolveDown(XkDeadBelowdot, 1, true, KeyModifiers.Control, [], ref dead, ref spacing);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ev, Is.Null);
            Assert.That(dead, Is.EqualTo('\u0300'));
        }
    }
}
