using Hydra.Config;
using Hydra.Keyboard;
using Hydra.Platform.Linux;
using Hydra.Platform.Linux.Wayland;

namespace Tests.Platform.Wayland;

[TestFixture]
public class WaylandShortcutsTests
{
    private const uint KeyLeftCtrl = 29, KeyLeftShift = 42, KeyLeftAlt = 56, KeyRightAlt = 100, KeyLeftMeta = 125;
    private const uint KeyL = 38, KeyLeft = 105, KeyUp = 103, KeyF13 = 183;
    private const KeyModifiers Chord3 = KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super;
    private const uint ControlL = 0xFFE3, ShiftL = 0xFFE1, AltL = 0xFFE9, SuperL = 0xFFEB, Level3Shift = 0xFE03, ModeSwitch = 0xFF7E;

    // each modifier on its usual key, as a stock keymap has them
    private static readonly FakeKeymap Stock = new(new Dictionary<uint, uint>
    {
        [KeyLeftCtrl] = ControlL,
        [KeyLeftShift] = ShiftL,
        [KeyLeftAlt] = AltL,
        [KeyLeftMeta] = SuperL,
        [KeyRightAlt] = Level3Shift,
    });

    // a keymap as the keys it has and the keysym each types alone; a keysym found only above level 0 is in Upper
    private sealed class FakeKeymap(Dictionary<uint, uint> keys, Dictionary<uint, uint>? upper = null) : IKeymapKeys
    {
        public uint? EvdevKeyFor(uint keysym) =>
            keys.Where(k => k.Value == keysym).Select(k => (uint?)k.Key).FirstOrDefault()
            ?? upper?.Where(k => k.Value == keysym).Select(k => (uint?)k.Key).FirstOrDefault();

        public uint BaseKeysym(uint evdevKey) => keys.GetValueOrDefault(evdevKey);
    }

    [Test]
    public void Trigger_OrdersTheModifiersAsTheSpec()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(WaylandShortcuts.Trigger(Chord3, "l"), Is.EqualTo("CTRL+ALT+LOGO+l"));
            Assert.That(WaylandShortcuts.Trigger(KeyModifiers.Shift | KeyModifiers.Control, "Page_Up"), Is.EqualTo("CTRL+SHIFT+Page_Up"));
            Assert.That(WaylandShortcuts.Trigger(KeyModifiers.None, "Scroll_Lock"), Is.EqualTo("Scroll_Lock"));
        }
    }

    [Test]
    public void Trigger_LockStatesAreNoPartOfIt()
    {
        Assert.That(WaylandShortcuts.Trigger(KeyModifiers.Control | KeyModifiers.NumLock | KeyModifiers.CapsLock, "l"), Is.EqualTo("CTRL+l"));
    }

    [Test]
    public void Trigger_IsLeftToTheUser_WhenTheSpecCannotSayIt()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(WaylandShortcuts.Trigger(KeyModifiers.AltGr, "l"), Is.Null);
            Assert.That(WaylandShortcuts.Trigger(KeyModifiers.Control, null), Is.Null);
        }
    }

    [Test]
    public void HyprlandBind_InHyprlandLua_SpellsTheTriggerAsHlBind()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(WaylandShortcuts.HyprlandBind(HyprlandConfig.Lua, "org.cathedral.Hydra", "ToggleCursorLock-0", "CTRL+ALT+LOGO+l"),
                Is.EqualTo("hl.bind(\"CTRL + ALT + SUPER + L\", hl.dsp.global(\"org.cathedral.Hydra:ToggleCursorLock-0\"))"));
            Assert.That(WaylandShortcuts.HyprlandBind(HyprlandConfig.Lua, "app", "CopyFiles-0", "SHIFT+F5"),
                Is.EqualTo("hl.bind(\"SHIFT + F5\", hl.dsp.global(\"app:CopyFiles-0\"))"));
        }
    }

    [Test]
    public void HyprlandBind_InHyprlandLua_AChordWithNoModifiersIsTheKeyAlone()
    {
        Assert.That(WaylandShortcuts.HyprlandBind(HyprlandConfig.Lua, "app", "LockSlaves-1", "Scroll_Lock"),
            Is.EqualTo("hl.bind(\"Scroll_Lock\", hl.dsp.global(\"app:LockSlaves-1\"))"));
    }

    [Test]
    public void HyprlandBind_InHyprlandConf_SpellsTheTriggerAsHyprlandMods()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(WaylandShortcuts.HyprlandBind(HyprlandConfig.Conf, "org.cathedral.Hydra", "ToggleCursorLock-0", "CTRL+ALT+LOGO+l"),
                Is.EqualTo("bind = CTRL ALT SUPER, l, global, org.cathedral.Hydra:ToggleCursorLock-0"));
            Assert.That(WaylandShortcuts.HyprlandBind(HyprlandConfig.Conf, "app", "LockSlaves-1", "Scroll_Lock"),
                Is.EqualTo("bind = , Scroll_Lock, global, app:LockSlaves-1"));
        }
    }

    [Test]
    public void HyprlandBind_WithNoTrigger_LeavesTheChordToTheUser()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(WaylandShortcuts.HyprlandBind(HyprlandConfig.Lua, "app", "CopyFiles-0", null),
                Is.EqualTo("hl.bind(\"<mods> + <key>\", hl.dsp.global(\"app:CopyFiles-0\"))"));
            Assert.That(WaylandShortcuts.HyprlandBind(HyprlandConfig.Conf, "app", "CopyFiles-0", null),
                Is.EqualTo("bind = <mods>, <key>, global, app:CopyFiles-0"));
        }
    }

    [Test]
    public void HydrasOwnKey_HasNoKeysym()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(HotkeyBinding.TryParse("Ctrl+MissionControl", out var binding, out _), Is.True);
            Assert.That(WaylandShortcuts.Keysym(binding!), Is.Zero);
        }
    }

    [Test]
    public void Chords_NameEachActionAndChordIndex()
    {
        var hotkeys = HotkeyBindings.Build(new Dictionary<string, List<string>> { ["ToggleCursorLock"] = ["Ctrl+Alt+Super+L", "ScrollLock"] });

        var chords = WaylandShortcuts.Chords(hotkeys);
        var lockChords = chords.Where(c => c.Action == HotkeyAction.ToggleCursorLock).ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(lockChords.Select(c => c.Id).ToList(), Is.EqualTo(["ToggleCursorLock-0", "ToggleCursorLock-1"]));
            Assert.That(chords.Select(c => c.Id).Distinct().Count(), Is.EqualTo(chords.Count));
            Assert.That(lockChords[1].Index, Is.EqualTo(1));
        }
    }

    [Test]
    public void Chords_AreOnlyTheActionsThatWorkAtHome()
    {
        var actions = WaylandShortcuts.Chords(HotkeyBindings.Build(null)).Select(c => c.Action).Distinct().ToList();

        Assert.That(actions, Is.EquivalentTo((HotkeyAction[])[HotkeyAction.ToggleCursorLock, HotkeyAction.LockSlaves, HotkeyAction.CopyFiles, HotkeyAction.PasteFiles]));
    }

    [Test]
    public void Chords_LeaveOutADisabledAction()
    {
        var hotkeys = HotkeyBindings.Build(new Dictionary<string, List<string>> { ["LockSlaves"] = [] });

        Assert.That(WaylandShortcuts.Chords(hotkeys).Select(c => c.Action), Has.No.Member(HotkeyAction.LockSlaves));
    }

    [Test]
    public void Keys_AreTheModifiersThenTheKey()
    {
        var keys = WaylandShortcuts.Keys(Stock, Chord3, KeyL);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(keys?.Downs.ToList(), Is.EqualTo([KeyLeftCtrl, KeyLeftAlt, KeyLeftMeta, KeyL]));
            Assert.That(keys?.Ups.ToList(), Is.EqualTo([KeyL, KeyLeftMeta, KeyLeftAlt, KeyLeftCtrl]));
        }
    }

    [Test]
    public void Keys_ShiftAndAltGr_TakeTheirOwnKeys()
    {
        var keys = WaylandShortcuts.Keys(Stock, KeyModifiers.Shift | KeyModifiers.AltGr, KeyF13);

        Assert.That(keys?.Downs.ToList(), Is.EqualTo([KeyLeftShift, KeyRightAlt, KeyF13]));
    }

    [Test]
    public void Keys_AreNone_WhenTheKeymapHasNoKey()
    {
        Assert.That(WaylandShortcuts.Keys(Stock, Chord3, null), Is.Null);
    }

    [Test]
    public void Keys_FollowAModifierTheKeymapMoved()
    {
        // altwin:swap_alt_win: Super on the left Alt key, Alt on the left Super key
        var swapped = new FakeKeymap(new Dictionary<uint, uint> { [KeyLeftCtrl] = ControlL, [KeyLeftAlt] = SuperL, [KeyLeftMeta] = AltL });

        Assert.That(WaylandShortcuts.Keys(swapped, Chord3, KeyL)?.Modifiers, Is.EqualTo([KeyLeftCtrl, KeyLeftMeta, KeyLeftAlt]));
    }

    [Test]
    public void Keys_TakeTheUsualKey_ForAModifierTheKeymapLacks()
    {
        var bare = new FakeKeymap([]);

        Assert.That(WaylandShortcuts.Keys(bare, Chord3 | KeyModifiers.Shift | KeyModifiers.AltGr, KeyL)?.Modifiers,
            Is.EqualTo([KeyLeftCtrl, KeyLeftAlt, KeyLeftShift, KeyLeftMeta, KeyRightAlt]));
    }

    [Test]
    public void Keys_TakeModeSwitch_ForAltGr_WhenTheKeymapHasNoLevel3Shift()
    {
        const uint keyMenu = 127;
        var keymap = new FakeKeymap(new Dictionary<uint, uint> { [keyMenu] = ModeSwitch });

        Assert.That(WaylandShortcuts.Keys(keymap, KeyModifiers.AltGr, KeyL)?.Modifiers, Is.EqualTo([keyMenu]));
    }

    [Test]
    public void Keys_TakeTheRightHandModifier_WhenTheKeymapHasNoLeftOne()
    {
        const uint altR = 0xFFEA;
        var keymap = new FakeKeymap(new Dictionary<uint, uint> { [KeyRightAlt] = altR });

        Assert.That(WaylandShortcuts.Keys(keymap, KeyModifiers.Alt, KeyL)?.Modifiers, Is.EqualTo([KeyRightAlt]));
    }

    [Test]
    public void Keys_SkipAKeyTypingTheModifierOnlyOnAHigherLevel()
    {
        // a Super on a shifted level needs Shift held, which the chord does not press
        const uint keyCompose = 127;
        var keymap = new FakeKeymap([], new Dictionary<uint, uint> { [keyCompose] = SuperL });

        Assert.That(WaylandShortcuts.Keys(keymap, KeyModifiers.Super, KeyL)?.Modifiers, Is.EqualTo([KeyLeftMeta]));
    }

    [Test]
    public void Held_OneChord_PressesAllAndReleasesInReverse()
    {
        var held = new HeldChords();
        var keys = WaylandShortcuts.Keys(Stock, Chord3, KeyL)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(held.Press("ToggleCursorLock-0", keys), Is.EqualTo([KeyLeftCtrl, KeyLeftAlt, KeyLeftMeta, KeyL]));
            Assert.That(held.Release("ToggleCursorLock-0"), Is.EqualTo([KeyL, KeyLeftMeta, KeyLeftAlt, KeyLeftCtrl]));
            Assert.That(held.Release("ToggleCursorLock-0"), Is.Empty);
        }
    }

    [Test]
    public void Held_OverlappingChords_ShareTheirModifiers()
    {
        // a second chord under the first's modifiers, the first let go first
        var held = new HeldChords();
        var left = WaylandShortcuts.Keys(Stock, Chord3, KeyLeft)!;
        var up = WaylandShortcuts.Keys(Stock, Chord3, KeyUp)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(held.Press("a-0", left), Is.EqualTo([KeyLeftCtrl, KeyLeftAlt, KeyLeftMeta, KeyLeft]));
            Assert.That(held.Press("b-0", up), Is.EqualTo([KeyUp]));
            Assert.That(held.Release("a-0"), Is.EqualTo([KeyLeft]));
            Assert.That(held.Release("b-0"), Is.EqualTo([KeyUp, KeyLeftMeta, KeyLeftAlt, KeyLeftCtrl]));
        }
    }

    [Test]
    public void Held_ARepeatedActivation_PressesNothing()
    {
        var held = new HeldChords();
        var keys = WaylandShortcuts.Keys(Stock, Chord3, KeyL)!;
        held.Press("ToggleCursorLock-0", keys);

        Assert.That(held.Press("ToggleCursorLock-0", keys), Is.Empty);
    }

    [Test]
    public void Held_ReleaseAll_LetsGoOfEverything()
    {
        var held = new HeldChords();
        held.Press("a-0", WaylandShortcuts.Keys(Stock, Chord3, KeyLeft)!);
        held.Press("b-0", WaylandShortcuts.Keys(Stock, Chord3, KeyUp)!);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(held.ReleaseAll(), Is.EqualTo([KeyUp, KeyLeft, KeyLeftMeta, KeyLeftAlt, KeyLeftCtrl]));
            Assert.That(held.Release("a-0"), Is.Empty);
        }
    }
}

// The chords through libxkbcommon: keysym names for the triggers, and the chord's keys through a real EvdevKeyResolver,
// as the router gets them at home. They self-skip off Linux and run in the test-linux CI job and run-tests.sh linux.
[TestFixture]
[Category("Linux")]
public class WaylandShortcutEventsTests
{
    private const uint KeyLeftCtrl = 29, KeyLeftShift = 42, KeyLeftAlt = 56, KeyRightAlt = 100, KeyLeftMeta = 125, KeyF13 = 183;

    [OneTimeSetUp]
    public void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Ignore("Requires Linux with libxkbcommon (run ./run-tests.sh linux)");
    }

    private static HotkeyBinding Chord(string text)
    {
        Assert.That(HotkeyBinding.TryParse(text, out var binding, out var error), Is.True, error);
        return binding!;
    }

    [TestCase("Ctrl+Alt+Super+L", "CTRL+ALT+LOGO+l")]
    [TestCase("Ctrl+Alt+Super+Left", "CTRL+ALT+LOGO+Left")]
    [TestCase("Shift+Ctrl+PageUp", "CTRL+SHIFT+Prior")]
    [TestCase("ScrollLock", "Scroll_Lock")]
    [TestCase("F13", "F13")]
    [TestCase("Alt+PrintScreen", "ALT+Print")]
    [TestCase("Ctrl+Space", "CTRL+space")]
    [TestCase("Ctrl++", "CTRL+plus")]
    [TestCase("Super+4", "LOGO+4")]
    [TestCase("Ctrl+AudioVolumeUp", "CTRL+XF86AudioRaiseVolume")]
    [TestCase("Ctrl+§", "CTRL+section")]
    public void Trigger_NamesTheKeyAsXkbcommonDoes(string chord, string trigger)
    {
        Assert.That(WaylandShortcuts.Trigger(Chord(chord)), Is.EqualTo(trigger));
    }

    [Test]
    public void Trigger_IsLeftToTheUser_ForHydrasOwnKey()
    {
        Assert.That(WaylandShortcuts.Trigger(Chord("Ctrl+MissionControl")), Is.Null);
    }

    [Test]
    public void Chord_PressesAsTheHotkeyItIs()
    {
        using var resolver = new EvdevKeyResolver(new LinuxInputConfig.XkbNames("us", "pc105", null, null));
        using var lookup = new XkbKeymapLookup(resolver.KeymapText());
        var binding = Chord("Ctrl+Alt+Super+K");
        var keys = WaylandShortcuts.Keys(lookup, binding.Modifiers, lookup.EvdevKeyFor(WaylandShortcuts.Keysym(binding)))!;
        var hotkeys = HotkeyBindings.Build(null);

        var downs = WaylandShortcuts.Events(resolver, keys.Downs, down: true);
        var ups = WaylandShortcuts.Events(resolver, keys.Ups, down: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(keys.Modifiers, Is.EqualTo([KeyLeftCtrl, KeyLeftAlt, KeyLeftMeta]));
            Assert.That(downs.Select(e => e.PhysicalKey).ToList(), Is.EqualTo(keys.Downs.Select(k => (int)k).ToList()));
            Assert.That(downs.Take(3).Select(e => e.Key).ToList(), Is.EqualTo(new SpecialKey?[] { SpecialKey.Control_L, SpecialKey.Alt_L, SpecialKey.Super_L }));
            Assert.That(downs[^1].Type, Is.EqualTo(KeyEventType.KeyDown));
            Assert.That(downs[^1].Character, Is.EqualTo('k'));
            Assert.That(hotkeys.Match(downs[^1]), Is.EqualTo(HotkeyAction.ToggleCursorLock));
            Assert.That(ups.Select(e => e.PhysicalKey).ToList(), Is.EqualTo(keys.Ups.Select(k => (int)k).ToList()));
            Assert.That(ups.Select(e => e.Type), Is.All.EqualTo(KeyEventType.KeyUp));
            Assert.That(HotkeyBinding.ChordModifiers(ups[^1]), Is.EqualTo(KeyModifiers.None));
        }
    }

    [TestCase("altwin:swap_alt_win", KeyLeftCtrl, KeyLeftMeta, KeyLeftAlt)]
    [TestCase("ctrl:swap_lalt_lctl", KeyLeftAlt, KeyLeftCtrl, KeyLeftMeta)]
    [TestCase("ctrl:nocaps", KeyLeftCtrl, KeyLeftAlt, KeyLeftMeta)]
    public void Chord_PressesAsTheHotkey_OnAKeymapWithRemappedModifiers(string options, uint ctrl, uint alt, uint super)
    {
        using var resolver = new EvdevKeyResolver(new LinuxInputConfig.XkbNames("us", "pc105", null, options));
        using var lookup = new XkbKeymapLookup(resolver.KeymapText());
        var binding = Chord("Ctrl+Alt+Super+K");
        var keys = WaylandShortcuts.Keys(lookup, binding.Modifiers, lookup.EvdevKeyFor(WaylandShortcuts.Keysym(binding)))!;

        var downs = WaylandShortcuts.Events(resolver, keys.Downs, down: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(keys.Modifiers, Is.EqualTo([ctrl, alt, super]));
            Assert.That(downs.Take(3).Select(e => e.Key).ToList(), Is.EqualTo(new SpecialKey?[] { SpecialKey.Control_L, SpecialKey.Alt_L, SpecialKey.Super_L }));
            Assert.That(downs[^1].Character, Is.EqualTo('k'));
            Assert.That(HotkeyBinding.ChordModifiers(downs[^1]), Is.EqualTo(KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super));
            Assert.That(HotkeyBindings.Build(null).Match(downs[^1]), Is.EqualTo(HotkeyAction.ToggleCursorLock));
        }
    }

    [Test]
    public void Chord_PressesShiftAndAltGr_OnTheirOwnKeys()
    {
        using var resolver = new EvdevKeyResolver(new LinuxInputConfig.XkbNames("de", "pc105", null, null));
        using var lookup = new XkbKeymapLookup(resolver.KeymapText());

        var keys = WaylandShortcuts.Keys(lookup, KeyModifiers.Shift | KeyModifiers.AltGr, KeyF13)!;
        var downs = WaylandShortcuts.Events(resolver, keys.Modifiers, down: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(keys.Modifiers, Is.EqualTo([KeyLeftShift, KeyRightAlt]));
            Assert.That(downs.Select(e => e.Key).ToList(), Is.EqualTo(new SpecialKey?[] { SpecialKey.Shift_L, SpecialKey.AltGr }));
        }
    }

    [Test]
    public void Chord_PressesAltGr_OnTheKeymapsLevel3Key_WhereRightAltIsAlt()
    {
        // plain us types Alt_R on the right Alt key and has ISO_Level3_Shift only on <LVL3>
        const uint keyLevel3 = 84;
        using var resolver = new EvdevKeyResolver(new LinuxInputConfig.XkbNames("us", "pc105", null, null));
        using var lookup = new XkbKeymapLookup(resolver.KeymapText());
        var binding = Chord("AltGr+F5");
        var keys = WaylandShortcuts.Keys(lookup, binding.Modifiers, lookup.EvdevKeyFor(WaylandShortcuts.Keysym(binding)))!;
        var hotkeys = HotkeyBindings.Build(new Dictionary<string, List<string>> { ["CopyFiles"] = ["AltGr+F5"] });

        var downs = WaylandShortcuts.Events(resolver, keys.Downs, down: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(keys.Modifiers, Is.EqualTo([keyLevel3]));
            Assert.That(downs[0].Key, Is.EqualTo(SpecialKey.AltGr));
            Assert.That(downs[^1].Key, Is.EqualTo(SpecialKey.F5));
            Assert.That(HotkeyBinding.ChordModifiers(downs[^1]), Is.EqualTo(KeyModifiers.AltGr));
            Assert.That(hotkeys.Match(downs[^1]), Is.EqualTo(HotkeyAction.CopyFiles));
        }
    }
}
