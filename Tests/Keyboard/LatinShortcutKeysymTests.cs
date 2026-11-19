using Hydra.Config;
using Hydra.Keyboard;
using Hydra.Platform.Linux;

namespace Tests.Keyboard;

// LatinShortcutKeysym's rule without a keymap: the X11 master feeds it XkbKeycodeToKeysym, which needs an X server
// holding a multi-group keymap, and the headless Xvfb run has only its default us one, so its rule is tested here
[TestFixture]
public class LatinShortcutKeysymTests
{
    private const ulong CyrillicEs = 0x6E3, LatinC = 0x63, LatinJ = 0x6A, LatinH = 0x68, Adiaeresis = 0xE4, F5 = 0xFFC2;
    private const ulong Period = 0x2E, Slash = 0x2F, Semicolon = 0x3B, GeorgianC = 0x010010EA;
    private const ulong IsoLevel3Shift = 0xFE03, DeadAcute = 0xFE51, At = 0x40, Two = 0x32;

    private static ulong Resolve(ulong keysym, int active, ulong[] bases, bool[] latin) =>
        LatinShortcutKeysym.Resolve(keysym, active, bases.Length, i => bases[i], i => latin[i], 0);

    [Test]
    public void AsciiKeysym_IsKept()
    {
        Assert.That(Resolve(LatinH, 1, [LatinC, LatinH], [true, true]), Is.EqualTo(LatinH));
    }

    [Test]
    public void LayoutIndependentKeysym_IsKept()
    {
        Assert.That(Resolve(F5, 1, [LatinC, F5], [true, false]), Is.EqualTo(F5));
    }

    [Test]
    public void NonAsciiKeysym_OnALatinLayout_IsKept()
    {
        Assert.That(Resolve(Adiaeresis, 1, [LatinC, Adiaeresis], [true, true]), Is.EqualTo(Adiaeresis));
    }

    [Test]
    public void NonLatinKeysym_TakesTheFirstLatinLayoutsBase()
    {
        Assert.That(Resolve(CyrillicEs, 0, [CyrillicEs, LatinC, LatinJ], [false, true, true]), Is.EqualTo(LatinC));
    }

    [Test]
    public void NonLatinKeysym_SkipsLayoutsTheKeyLacks()
    {
        Assert.That(Resolve(CyrillicEs, 1, [0, CyrillicEs, LatinJ], [true, false, true]), Is.EqualTo(LatinJ));
    }

    [Test]
    public void NonLatinKeysym_TakesALatinLayoutsNonAsciiBase()
    {
        // ru,de: the key typing ä on de gives Ctrl+ä, which a de slave has
        Assert.That(Resolve(CyrillicEs, 0, [CyrillicEs, Adiaeresis], [false, true]), Is.EqualTo(Adiaeresis));
    }

    [Test]
    public void AsciiKeysym_OnANonLatinLayout_TakesTheLatinLayoutsBase()
    {
        // ru,us: ru's slash key types '.', us's '/'
        Assert.That(Resolve(Period, 0, [Period, Slash], [false, true]), Is.EqualTo(Slash));
    }

    [Test]
    public void AsciiKeysym_SkipsNonLatinLayouts()
    {
        // gr,ru,us: an ASCII base on another non-Latin layout is not the Latin one
        Assert.That(Resolve(Semicolon, 0, [Semicolon, Period, LatinC], [false, false, true]), Is.EqualTo(LatinC));
    }

    [Test]
    public void UnicodeKeysym_OnANonLatinLayout_TakesTheLatinLayoutsBase()
    {
        // ge,us: ge's C key types U+10EA, a Unicode keysym
        Assert.That(Resolve(GeorgianC, 0, [GeorgianC, LatinC], [false, true]), Is.EqualTo(LatinC));
    }

    [TestCase(2u)]
    [TestCase(3u)]
    [TestCase(4u)]
    public void AltGrLevel_OnANonLatinLayout_KeepsItsKeysym(uint level)
    {
        // ru(typo),us: Ctrl+AltGr+2 types @ (level 2); us's level 0 there is '2'
        Assert.That(LatinShortcutKeysym.Resolve(At, 0, 2, _ => Two, i => i == 1, 0, level), Is.EqualTo(At));
    }

    [TestCase(0u)]
    [TestCase(1u)]
    public void BaseOrShiftLevel_OnANonLatinLayout_TakesTheLatinLayoutsBase(uint level)
    {
        ulong[] bases = [CyrillicEs, LatinC];
        Assert.That(LatinShortcutKeysym.Resolve(CyrillicEs, 0, 2, i => bases[i], i => i == 1, 0, level), Is.EqualTo(LatinC));
    }

    [Test]
    public void IsPrintable_CoversUnicodeKeysymsButNotC1()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(LatinShortcutKeysym.IsPrintable(GeorgianC), Is.True);
            Assert.That(LatinShortcutKeysym.IsPrintable(0x0110FFFF), Is.True);
            Assert.That(LatinShortcutKeysym.IsPrintable(0x01000085), Is.False);
            Assert.That(LatinShortcutKeysym.IsPrintable(0x0100001F), Is.False);
            Assert.That(LatinShortcutKeysym.IsPrintable(F5), Is.False);
            Assert.That(LatinShortcutKeysym.IsPrintable(IsoLevel3Shift), Is.False);
            Assert.That(LatinShortcutKeysym.IsPrintable(DeadAcute), Is.True);
        }
    }

    [Test]
    public void IsLatinLayout_NeedsLetterKeys()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(LatinShortcutKeysym.IsLatinLayout(8, 255, k => k is >= 38 and < 64 ? 'a' + (ulong)(k - 38) : 0), Is.True);
            Assert.That(LatinShortcutKeysym.IsLatinLayout(8, 255, k => k is >= 38 and < 64 ? CyrillicEs : 0), Is.False);
        }
    }
}

// shortcuts on a non-Latin layout through a real EvdevKeyResolver (the evdev and Wayland masters). they need
// libxkbcommon, so they self-skip off Linux and run in the test-linux CI job and run-tests.sh linux.
[TestFixture]
[Category("Linux")]
public class EvdevLatinShortcutTests
{
    private const uint KeyQ = 16, KeyY = 21, KeyG = 34, KeyJ = 36, KeyL = 38, KeyC = 46, KeyV = 47;
    private const uint Key1 = 2, Key2 = 3, KeyMinus = 12, KeyLeftBrace = 26, KeySemicolon = 39, KeyApostrophe = 40, KeyGrave = 41;
    private const uint KeyComma = 51, KeyDot = 52, KeySlash = 53;
    private const uint KeyLeftCtrl = 29, KeyLeftShift = 42, KeyLeftAlt = 56, KeyCapsLock = 58, KeyLeftMeta = 125, KeyRightAlt = 100;
    private const uint KeyF1 = 59, KeyF2 = 60, KeyNumLock = 69, KeyKpPlus = 78;

    [OneTimeSetUp]
    public void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Ignore("Requires Linux with libxkbcommon (run ./run-tests.sh linux)");
    }

    // caps lock switches to the next group, so a test can make the second layout active
    private static EvdevKeyResolver Resolver(string layout, string? variant = null) =>
        new(new LinuxInputConfig.XkbNames(layout, "pc105", variant, "grp:caps_toggle"));

    private static void NextGroup(EvdevKeyResolver resolver)
    {
        _ = resolver.Resolve(KeyCapsLock, 1);
        _ = resolver.Resolve(KeyCapsLock, 0);
    }

    // presses the held keys, then the key, and returns the key's down; releases all so the resolver is clean again
    private static KeyEvent Press(EvdevKeyResolver resolver, uint key, params uint[] held)
    {
        foreach (var h in held) _ = resolver.Resolve(h, 1);
        var down = resolver.Resolve(key, 1)!;
        _ = resolver.Resolve(key, 0);
        foreach (var h in Enumerable.Reverse(held)) _ = resolver.Resolve(h, 0);
        return down[^1]!;
    }

    [Test]
    public void UsRu_RuActive_TypesCyrillic()
    {
        using var resolver = Resolver("us,ru");
        NextGroup(resolver);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Press(resolver, KeyL).Character, Is.EqualTo('д'));
            Assert.That(Press(resolver, KeyQ, KeyLeftShift).Character, Is.EqualTo('Й'));
        }
    }

    [Test]
    public void UsRu_RuActive_ShortcutsTakeTheLatinLayoutsKey()
    {
        using var resolver = Resolver("us,ru");
        NextGroup(resolver);

        var copy = Press(resolver, KeyC, KeyLeftCtrl);
        var cursorLock = Press(resolver, KeyL, KeyLeftCtrl, KeyLeftAlt, KeyLeftMeta);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(copy.Character, Is.EqualTo('c'));
            Assert.That(copy.Modifiers & KeyModifiers.Control, Is.EqualTo(KeyModifiers.Control));
            Assert.That(cursorLock.Character, Is.EqualTo('l'));
            Assert.That(HotkeyBindings.Build(null).Match(cursorLock), Is.EqualTo(HotkeyAction.ToggleCursorLock));
        }
    }

    [Test]
    public void RuUs_RuActive_ShortcutTakesTheLaterLatinLayoutsKey()
    {
        using var resolver = Resolver("ru,us");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Press(resolver, KeyC).Character, Is.EqualTo('с'));
            Assert.That(Press(resolver, KeyC, KeyLeftCtrl).Character, Is.EqualTo('c'));
        }
    }

    [Test]
    public void RuOnly_ShortcutTakesTheUsReferenceKey()
    {
        using var resolver = Resolver("ru");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Press(resolver, KeyC, KeyLeftCtrl).Character, Is.EqualTo('c'));
            Assert.That(Press(resolver, KeyV, KeyLeftCtrl).Character, Is.EqualTo('v'));
            Assert.That(Press(resolver, KeyC).Character, Is.EqualTo('с'));
            Assert.That(Press(resolver, KeyV).Character, Is.EqualTo('м'));
        }
    }

    [Test]
    public void Dvorak_ShortcutKeepsItsOwnLetter()
    {
        // the qwerty J key types h on dvorak
        using var resolver = Resolver("us", "dvorak");
        Assert.That(Press(resolver, KeyJ, KeyLeftCtrl).Character, Is.EqualTo('h'));
    }

    [Test]
    public void German_ShortcutKeepsItsOwnLetter()
    {
        // de swaps Y and Z: the qwerty Y key is de's Z
        using var resolver = Resolver("de");
        Assert.That(Press(resolver, KeyY, KeyLeftCtrl).Character, Is.EqualTo('z'));
    }

    [Test]
    public void German_CtrlAltGr_KeepsTheAltGrCharacter()
    {
        // de: <AD01> { [ q, Q, at, Greek_OMEGA ] }, AltGr on ralt
        using var resolver = Resolver("de");
        Assert.That(Press(resolver, KeyQ, KeyLeftCtrl, KeyRightAlt).Character, Is.EqualTo('@'));
    }

    // symbols/pc's srvr_ctrl(fkey2vt) and keypad(operators) put F1-F12 and the keypad operators on type CTRL+ALT, whose
    // Control+Alt level is XF86_Switch_VT_n or XF86_Next_VMode on every pc105 keymap: a shortcut's level never takes
    // Control, Alt or Super
    private static EvdevKeyResolver PlainUs() => new(new LinuxInputConfig.XkbNames("us", "pc105", null, null));

    [Test]
    public void Us_CtrlAltF1_IsF1()
    {
        using var resolver = PlainUs();
        Assert.That(Press(resolver, KeyF1, KeyLeftCtrl, KeyLeftAlt).Key, Is.EqualTo(SpecialKey.F1));
    }

    [Test]
    public void Us_CtrlAltSuperF2_IsF2()
    {
        using var resolver = PlainUs();
        Assert.That(Press(resolver, KeyF2, KeyLeftCtrl, KeyLeftAlt, KeyLeftMeta).Key, Is.EqualTo(SpecialKey.F2));
    }

    [Test]
    public void Us_NumLockOn_CtrlAltKpAdd_IsKpAdd()
    {
        using var resolver = PlainUs();
        _ = resolver.Resolve(KeyNumLock, 1);
        _ = resolver.Resolve(KeyNumLock, 0);

        Assert.That(Press(resolver, KeyKpPlus, KeyLeftCtrl, KeyLeftAlt).Key, Is.EqualTo(SpecialKey.KP_Add));
    }

    [Test]
    public void Us_CtrlAltSuperL_IsTheCursorLockHotkey()
    {
        using var resolver = PlainUs();
        var chord = Press(resolver, KeyL, KeyLeftCtrl, KeyLeftAlt, KeyLeftMeta);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chord.Character, Is.EqualTo('l'));
            Assert.That(HotkeyBindings.Build(null).Match(chord), Is.EqualTo(HotkeyAction.ToggleCursorLock));
        }
    }

    [Test]
    public void UsRu_SetLayout_TypesTheCompositorsGroup()
    {
        using var resolver = Resolver("us,ru");

        resolver.SetLayout(1);
        var ru = Press(resolver, KeyG);
        var ruShifted = Press(resolver, KeyG, KeyLeftShift);
        var copy = Press(resolver, KeyC, KeyLeftCtrl);
        resolver.SetLayout(0);
        var us = Press(resolver, KeyG);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ru.Character, Is.EqualTo('п'));
            Assert.That(ruShifted.Character, Is.EqualTo('П'));
            Assert.That(copy.Character, Is.EqualTo('c'));
            Assert.That(us.Character, Is.EqualTo('g'));
        }
    }

    [Test]
    public void UsRu_SetLayout_KeepsHeldModifiers()
    {
        using var resolver = Resolver("us,ru");

        _ = resolver.Resolve(KeyLeftShift, 1);
        resolver.SetLayout(1);
        var down = resolver.Resolve(KeyG, 1)![^1]!;

        Assert.That(down.Character, Is.EqualTo('П'));
    }

    [Test]
    public void UsRu_AGroupToggleAfterSetLayout_StillSwitches()
    {
        using var resolver = Resolver("us,ru");

        resolver.SetLayout(1);
        NextGroup(resolver);

        Assert.That(Press(resolver, KeyG).Character, Is.EqualTo('g'));
    }

    [Test]
    public void UsRu_SetLayoutPastTheLayouts_IsIgnored()
    {
        using var resolver = Resolver("us,ru");

        resolver.SetLayout(1);
        resolver.SetLayout(2);

        Assert.That(Press(resolver, KeyG).Character, Is.EqualTo('п'));
    }

    [Test]
    public void UsRu_UsActive_IsUnchanged()
    {
        using var resolver = Resolver("us,ru");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Press(resolver, KeyL).Character, Is.EqualTo('l'));
            Assert.That(Press(resolver, KeyC, KeyLeftCtrl).Character, Is.EqualTo('c'));
            Assert.That(HotkeyBindings.Build(null).Match(Press(resolver, KeyL, KeyLeftCtrl, KeyLeftAlt, KeyLeftMeta)),
                Is.EqualTo(HotkeyAction.ToggleCursorLock));
        }
    }

    [TestCase(KeySlash, '/')]
    [TestCase(KeyDot, '.')]
    [TestCase(KeyComma, ',')]
    [TestCase(KeyGrave, '`')]
    [TestCase(KeyLeftBrace, '[')]
    [TestCase(KeySemicolon, ';')]
    [TestCase(KeyApostrophe, '\'')]
    [TestCase(Key1, '1')]
    public void RuOnly_PunctuationAndDigitShortcutsTakeTheUsReferenceKey(uint key, char expected)
    {
        // ru's slash key types '.', as its dot key (ю) would under Ctrl: both must not send Ctrl+.
        using var resolver = Resolver("ru");
        Assert.That(Press(resolver, key, KeyLeftCtrl).Character, Is.EqualTo(expected));
    }

    [Test]
    public void RuOnly_CtrlShiftShortcut_KeepsShiftAsUsDoes()
    {
        using var ru = Resolver("ru");
        using var us = Resolver("us");

        var ruCopy = Press(ru, KeyC, KeyLeftCtrl, KeyLeftShift);
        var usCopy = Press(us, KeyC, KeyLeftCtrl, KeyLeftShift);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ruCopy.Character, Is.EqualTo('c'));
            Assert.That(ruCopy.Character, Is.EqualTo(usCopy.Character));
            Assert.That(ruCopy.Modifiers & KeyModifiers.Shift, Is.EqualTo(KeyModifiers.Shift));
            Assert.That(ruCopy.Modifiers, Is.EqualTo(usCopy.Modifiers));
        }
    }

    [Test]
    public void RuOnly_SuperShortcutTakesTheUsReferenceKey()
    {
        using var resolver = Resolver("ru");
        Assert.That(Press(resolver, KeyL, KeyLeftMeta).Character, Is.EqualTo('l'));
    }

    [Test]
    public void RuOnly_AltWithoutCtrlOrSuper_TypesCyrillic()
    {
        using var resolver = Resolver("ru");
        Assert.That(Press(resolver, KeyC, KeyLeftAlt).Character, Is.EqualTo('с'));
    }

    [Test]
    public void RuOnly_TypingAfterAShortcut_IsCyrillic()
    {
        using var resolver = Resolver("ru");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Press(resolver, KeyC, KeyLeftCtrl).Character, Is.EqualTo('c'));
            Assert.That(Press(resolver, KeyC).Character, Is.EqualTo('с'));
        }
    }

    [Test]
    public void RuUs_UsActive_ShortcutKeepsItsOwnPunctuation()
    {
        using var resolver = Resolver("ru,us");
        NextGroup(resolver);
        Assert.That(Press(resolver, KeySlash, KeyLeftCtrl).Character, Is.EqualTo('/'));
    }

    [Test]
    public void RuUaUs_RuActive_ShortcutTakesTheLastLatinLayoutsKey()
    {
        using var resolver = Resolver("ru,ua,us");
        Assert.That(Press(resolver, KeyC, KeyLeftCtrl).Character, Is.EqualTo('c'));
    }

    [Test]
    public void RuUa_NoLatinLayout_ShortcutTakesTheUsReferenceKey()
    {
        using var resolver = Resolver("ru,ua");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Press(resolver, KeyC, KeyLeftCtrl).Character, Is.EqualTo('c'));
            Assert.That(Press(resolver, KeySlash, KeyLeftCtrl).Character, Is.EqualTo('/'));
        }
    }

    [Test]
    public void Greek_ShortcutTakesTheUsReferenceKey()
    {
        // gr's Q key types ';'
        using var resolver = Resolver("gr");
        Assert.That(Press(resolver, KeyQ, KeyLeftCtrl).Character, Is.EqualTo('q'));
    }

    [Test]
    public void Georgian_ShortcutTakesTheUsReferenceKey()
    {
        // ge's letters are Unicode keysyms (0x01000000 + code point)
        using var resolver = Resolver("ge");
        var reference = (char)LatinShortcutKeysym.ReferenceBase(KeyC + 8);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reference, Is.EqualTo('c'));
            Assert.That(Press(resolver, KeyC, KeyLeftCtrl).Character, Is.EqualTo(reference));
        }
    }

    [Test]
    public void Dvorak_ShortcutKeepsItsOwnPunctuation()
    {
        // the qwerty Q key types ' on dvorak
        using var resolver = Resolver("us", "dvorak");
        Assert.That(Press(resolver, KeyQ, KeyLeftCtrl).Character, Is.EqualTo('\''));
    }

    [Test]
    public void German_ShortcutKeepsItsOwnPunctuation()
    {
        // the qwerty minus key types ß on de
        using var resolver = Resolver("de");
        Assert.That(Press(resolver, KeyMinus, KeyLeftCtrl).Character, Is.EqualTo('ß'));
    }

    [TestCase("us", null, true)]
    [TestCase("de", null, true)]
    [TestCase("us", "dvorak", true)]
    [TestCase("fr", null, true)]
    [TestCase("ru", null, false)]
    [TestCase("gr", null, false)]
    [TestCase("ua", null, false)]
    [TestCase("ge", null, false)]
    public void LatinLayouts_OnARealKeymap(string layout, string? variant, bool latin)
    {
        using var keymap = XkbKeymap.FromNames(new LinuxInputConfig.XkbNames(layout, "pc105", variant, null));
        Assert.That(LatinShortcutKeysym.LatinLayouts(keymap.Handle), Is.EqualTo((bool[])[latin]));
    }

    [Test]
    public void ReferenceKeyFor_FindsUsKeys()
    {
        const uint cyrillicEs = 0x6E3;
        var c = LatinShortcutKeysym.ReferenceKeyFor('c');
        var slash = LatinShortcutKeysym.ReferenceKeyFor('/');
        var upperC = LatinShortcutKeysym.ReferenceKeyFor('C');

        using (Assert.EnterMultipleScope())
        {
            Assert.That(c?.EvdevKey, Is.EqualTo(KeyC));
            Assert.That(c?.Shift, Is.False);
            Assert.That(slash?.EvdevKey, Is.EqualTo(KeySlash));
            Assert.That(slash?.Shift, Is.False);
            Assert.That(upperC?.EvdevKey, Is.EqualTo(KeyC));
            Assert.That(upperC?.Shift, Is.True);
            Assert.That(LatinShortcutKeysym.ReferenceKeyFor(cyrillicEs), Is.Null);
        }
    }

    [Test]
    public void ReferenceKeyFor_AnswersFromTheCache_MissesIncluded()
    {
        const uint cyrillicEf = 0x6C6;
        var first = LatinShortcutKeysym.ReferenceKeyFor('v');
        var miss = LatinShortcutKeysym.ReferenceKeyFor(cyrillicEf);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first?.EvdevKey, Is.EqualTo(KeyV));
            Assert.That(LatinShortcutKeysym.IsReferenceKeyCached('v'), Is.True);
            Assert.That(LatinShortcutKeysym.ReferenceKeyFor('v'), Is.SameAs(first));
            Assert.That(miss, Is.Null);
            Assert.That(LatinShortcutKeysym.IsReferenceKeyCached(cyrillicEf), Is.True);
            Assert.That(LatinShortcutKeysym.ReferenceKeyFor(cyrillicEf), Is.Null);
        }
    }

    // the shortcut keysym a key gives under an xkb state with the held keys down, as EvdevKeyResolver asks it
    private static ulong ShortcutKeysym(string layout, string? variant, uint key, params uint[] held)
    {
        using var keymap = XkbKeymap.FromNames(new LinuxInputConfig.XkbNames(layout, "pc105", variant, null));
        var state = EvdevNativeMethods.xkb_state_new(keymap.Handle);
        try
        {
            foreach (var h in held) _ = EvdevNativeMethods.xkb_state_update_key(state, h + 8, EvdevNativeMethods.XKB_KEY_DOWN);
            var latin = LatinShortcutKeysym.LatinLayouts(keymap.Handle);
            var own = EvdevNativeMethods.xkb_state_key_get_one_sym(state, key + 8);
            return LatinShortcutKeysym.ForState(keymap.Handle, state, key + 8, own, i => i >= 0 && i < latin.Length && latin[i],
                LatinShortcutKeysym.LevelMods(keymap.Handle));
        }
        finally
        {
            EvdevNativeMethods.xkb_state_unref(state);
        }
    }

    [Test]
    public void RuTypo_CtrlAltGr_KeepsTheAltGrKeysym()
    {
        // ru(typo): <AE02> { [ 2, quotedbl, at, apostrophe ] }, AltGr on ralt (level3(ralt_switch))
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ShortcutKeysym("ru", "typo", Key2, KeyLeftCtrl, KeyRightAlt), Is.EqualTo((ulong)'@'));
            Assert.That(ShortcutKeysym("ru", "typo", Key2, KeyLeftCtrl, KeyLeftShift, KeyRightAlt), Is.EqualTo((ulong)'@'));
            Assert.That(ShortcutKeysym("ru,us", "typo,", Key2, KeyLeftCtrl, KeyRightAlt), Is.EqualTo((ulong)'@'));
        }
    }

    [Test]
    public void RuTypo_CtrlWithoutAltGr_TakesTheLatinKey()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ShortcutKeysym("ru", "typo", KeyC, KeyLeftCtrl), Is.EqualTo((ulong)'c'));
            Assert.That(ShortcutKeysym("ru", "typo", KeyC, KeyLeftCtrl, KeyLeftShift), Is.EqualTo((ulong)'c'));
            Assert.That(ShortcutKeysym("ru,us", "typo,", KeySlash, KeyLeftCtrl), Is.EqualTo((ulong)'/'));
        }
    }

    [Test]
    public void RuTypo_CtrlAltGr_OnAKeyWithNoAltGrLevel_TakesTheLatinKey()
    {
        // ru(typo) gives <AB03> (с) no third level, so AltGr leaves it at its base
        Assert.That(ShortcutKeysym("ru", "typo", KeyC, KeyLeftCtrl, KeyRightAlt), Is.EqualTo((ulong)'c'));
    }
}
