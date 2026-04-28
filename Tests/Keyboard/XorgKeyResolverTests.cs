using Hydra.Keyboard;
using Hydra.Platform.Linux;

namespace Tests.Keyboard;

[TestFixture]
public class XorgKeyResolverTests
{
    // XK_ keysym constants used in tests
    // ReSharper disable InconsistentNaming
    private const ulong XK_dead_grave = 0xFE50;  // Combining=U+0300, Spacing='`'
    private const ulong XK_dead_acute = 0xFE51;  // Combining=U+0301, Spacing='´'
    private const ulong XK_dead_belowdot = 0xFE60; // Combining=U+0323, Spacing='\0' (no spacing form)
    private const ulong XK_Tab = 0xFF09;  // → SpecialKey.Tab  (non-modifier special)
    private const ulong XK_Shift_L = 0xFFE1;  // → SpecialKey.Shift_L (modifier — transparent)
    // ReSharper restore InconsistentNaming

    // -- TakeDeadKeySpacing --

    [Test]
    public void TakeDeadKeySpacing_WithPendingAndSpacingForm_EmitsSpacingEvent()
    {
        var dead = '\u0300';      // combining grave
        var spacing = '\u0060';   // `
        var ev = XorgKeyResolver.TakeDeadKeySpacing(ref dead, ref spacing);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ev, Is.Not.Null);
            Assert.That(ev![0]!.Character, Is.EqualTo('`'));
            Assert.That(ev[0]!.Modifiers, Is.EqualTo(KeyModifiers.None));
            Assert.That(ev[0]!.Type, Is.EqualTo(KeyEventType.KeyDown));
            Assert.That(ev[1]!.Type, Is.EqualTo(KeyEventType.KeyUp));
            Assert.That(dead, Is.EqualTo('\0'));
            Assert.That(spacing, Is.EqualTo('\0'));
        }
    }

    [Test]
    public void TakeDeadKeySpacing_WithNoPendingDead_ReturnsNull()
    {
        var dead = '\0';
        var spacing = '`';
        var ev = XorgKeyResolver.TakeDeadKeySpacing(ref dead, ref spacing);
        Assert.That(ev, Is.Null);
    }

    [Test]
    public void TakeDeadKeySpacing_WithNoSpacingForm_ReturnsNullAndClearsState()
    {
        // dead_belowdot has no spacing form; state must still be cleared
        var dead = '\u0323';
        var spacing = '\0';
        var ev = XorgKeyResolver.TakeDeadKeySpacing(ref dead, ref spacing);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ev, Is.Null);
            Assert.That(dead, Is.EqualTo('\0'));
        }
    }

    // -- FlushDeadKeyBeforeSpecial --

    [Test]
    public void FlushDeadKeyBeforeSpecial_PendingDeadAndTabKeysym_FlushesSpacingForm()
    {
        var dead = '\u0300';     // combining grave (from dead_grave)
        var spacing = '\u0060';  // `
        var ev = XorgKeyResolver.FlushDeadKeyBeforeSpecial(XK_Tab, ref dead, ref spacing);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ev, Is.Not.Null);
            Assert.That(ev![0]!.Character, Is.EqualTo('`'));
            Assert.That(dead, Is.EqualTo('\0'));
        }
    }

    [Test]
    public void FlushDeadKeyBeforeSpecial_PendingDeadAndModifierKeysym_ReturnsNullModifierTransparent()
    {
        // modifier keys must not abort dead key composition
        var dead = '\u0300';
        var spacing = '`';
        var ev = XorgKeyResolver.FlushDeadKeyBeforeSpecial(XK_Shift_L, ref dead, ref spacing);
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
        var ev = XorgKeyResolver.FlushDeadKeyBeforeSpecial(XK_Tab, ref dead, ref spacing);
        Assert.That(ev, Is.Null);
    }

    [Test]
    public void FlushDeadKeyBeforeSpecial_PendingDeadNoSpacingFormAndTab_ReturnsNullClearsState()
    {
        // dead_belowdot (no spacing form) + Tab: silently dropped, state cleared
        var dead = '\u0323';
        var spacing = '\0';
        var ev = XorgKeyResolver.FlushDeadKeyBeforeSpecial(XK_Tab, ref dead, ref spacing);
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
        XorgKeyResolver.ResolveKeysym(XK_dead_grave, 1, keyDownId, ref dead, ref spacing, KeyModifiers.None, KeyEventType.KeyDown, trackDeadKey: true);
        Assert.That(dead, Is.EqualTo('\u0300'));  // grave pending

        var ev = XorgKeyResolver.ResolveKeysym(XK_dead_acute, 2, keyDownId, ref dead, ref spacing, KeyModifiers.None, KeyEventType.KeyDown, trackDeadKey: true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ev, Is.Not.Null);
            Assert.That(ev![0]!.Character, Is.EqualTo('\u0060'), "spacing form of dead_grave is backtick (U+0060)");
            Assert.That(ev[0]!.Modifiers, Is.EqualTo(KeyModifiers.None));
            Assert.That(ev[0]!.Type, Is.EqualTo(KeyEventType.KeyDown));
            Assert.That(ev[1]!.Type, Is.EqualTo(KeyEventType.KeyUp));
        }
    }

    [Test]
    public void ResolveKeysym_DeadPlusDead_SecondDeadBecomesNewPending()
    {
        var dead = '\0';
        var spacing = '\0';
        var keyDownId = new Dictionary<uint, CharClassification>();
        XorgKeyResolver.ResolveKeysym(XK_dead_grave, 1, keyDownId, ref dead, ref spacing, KeyModifiers.None, KeyEventType.KeyDown, trackDeadKey: true);

        XorgKeyResolver.ResolveKeysym(XK_dead_acute, 2, keyDownId, ref dead, ref spacing, KeyModifiers.None, KeyEventType.KeyDown, trackDeadKey: true);
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
        XorgKeyResolver.ResolveKeysym(XK_dead_grave, 1, keyDownId, ref dead, ref spacing, KeyModifiers.None, KeyEventType.KeyDown, trackDeadKey: true);
        XorgKeyResolver.ResolveKeysym(XK_dead_acute, 2, keyDownId, ref dead, ref spacing, KeyModifiers.None, KeyEventType.KeyDown, trackDeadKey: true);

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
        XorgKeyResolver.ResolveKeysym(XK_dead_belowdot, 1, keyDownId, ref dead, ref spacing, KeyModifiers.None, KeyEventType.KeyDown, trackDeadKey: true);

        var ev = XorgKeyResolver.ResolveKeysym(XK_dead_grave, 2, keyDownId, ref dead, ref spacing, KeyModifiers.None, KeyEventType.KeyDown, trackDeadKey: true);
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

    // -- shortcutFlush path in Resolve() --
    // Resolve() contains: if (isShortcut && _pendingDeadKey != '\0') shortcutFlush = TakeDeadKeySpacing(...)
    // the tests below simulate that exact conditional using the same static helpers.

    [Test]
    public void ShortcutFlush_DeadKeyPendingWithCtrlShortcut_FlushesSpacingFormBeforeShortcut()
    {
        var dead = '\0';
        var spacing = '\0';
        var keyDownId = new Dictionary<uint, CharClassification>();

        // step 1: dead_grave pressed — pending dead key set
        XorgKeyResolver.ResolveKeysym(XK_dead_grave, 1, keyDownId, ref dead, ref spacing, KeyModifiers.None, KeyEventType.KeyDown, trackDeadKey: true);
        Assert.That(dead, Is.EqualTo('\u0300'), "dead_grave must leave combining grave pending");

        // step 2: isShortcut=true (Ctrl held) — shortcutFlush triggers TakeDeadKeySpacing
        var flush = XorgKeyResolver.TakeDeadKeySpacing(ref dead, ref spacing);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(flush, Is.Not.Null, "pending dead key must be flushed before Ctrl shortcut");
            Assert.That(flush![0]!.Character, Is.EqualTo('`'), "spacing form of dead_grave is backtick");
            Assert.That(flush[0]!.Type, Is.EqualTo(KeyEventType.KeyDown));
            Assert.That(flush[1]!.Type, Is.EqualTo(KeyEventType.KeyUp));
            Assert.That(dead, Is.EqualTo('\0'), "dead key state cleared after flush");
        }
    }

    [Test]
    public void ShortcutFlush_DeadKeyPendingWithCtrlShortcut_SubsequentDeadFlushSeesCleanState()
    {
        // after shortcutFlush fires and clears _pendingDeadKey, FlushDeadKeyBeforeSpecial returns null —
        // confirming the shortcutFlush ?? deadFlush ordering guarantee (no double-flush).
        var dead = '\u0300';
        var spacing = '\u0060';

        _ = XorgKeyResolver.TakeDeadKeySpacing(ref dead, ref spacing);  // simulate shortcutFlush

        // deadFlush path: _pendingDeadKey is now '\0', so FlushDeadKeyBeforeSpecial must return null
        var deadFlush = XorgKeyResolver.FlushDeadKeyBeforeSpecial(XK_Tab, ref dead, ref spacing);
        Assert.That(deadFlush, Is.Null, "deadFlush must be null after shortcutFlush cleared the dead key state");
    }

    [Test]
    public void ShortcutFlush_NoPendingDeadKey_NoFlush()
    {
        var dead = '\0';
        var spacing = '\0';

        // no dead key pending — TakeDeadKeySpacing must return null (no spurious flush on shortcuts)
        var flush = XorgKeyResolver.TakeDeadKeySpacing(ref dead, ref spacing);
        Assert.That(flush, Is.Null, "no flush when no dead key pending");
    }
}
