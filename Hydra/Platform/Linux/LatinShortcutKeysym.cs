using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Hydra.Platform.Linux;

// the keysym a shortcut (Ctrl or Super held) sends from a key on a non-Latin layout, as GNOME, KDE and the X
// toolkits pick it: the key's base keysym in the first Latin layout of the keymap, else in a us reference keymap by
// the same keycode. every printable keysym is converted, ASCII punctuation and digits included, so Ctrl+C on ru
// sends Ctrl+c and Ctrl on ru's slash key (base '.') sends Ctrl+/ rather than Ctrl+., which a Latin slave can press
// and a Latin hotkey matches. a Latin layout keeps its own keysyms, its non-ASCII ones included: Ctrl+J on Dvorak
// stays Ctrl+J (not Ctrl+C), and Ctrl+ä on de stays Ctrl+ä.
internal static class LatinShortcutKeysym
{
    // a layout with letters a-z on at least this many keys types Latin; one with none or a stray few does not
    private const int LatinLetterKeys = 13;

    // compiled once on first need and kept for the process; null where xkbcommon or the us layout is missing
    private static readonly Lazy<XkbKeymap?> Reference = new(CompileReference);

    // ReferenceKeyFor's answers by keysym, null for one the reference lacks; the slave asks from its injection threads
    private static readonly ConcurrentDictionary<uint, XkbKeyStroke?> ReferenceKeys = new();

    // xkb_keymap_key_get_mods_for_level masks read per level; stock types have at most a few
    private const int MaxMasks = 32;

    // a printable keysym, ASCII or beyond, or a Unicode keysym (0x01000000 + a printable code point, xkeyboard-config's
    // for letters with no legacy keysym, as on ge and am); F-keys, modifiers and the like (0xFF00+) are layout-independent,
    // and so are the ISO lock, group and level keysyms (0xFE01-0xFE34), though dead keys (0xFE50+) stay printable
    internal static bool IsPrintable(ulong keysym) =>
        keysym is (>= 0x20 and < 0xFF00 and not (>= 0xFE01 and <= 0xFE34))
            or (>= 0x01000020 and <= 0x0110FFFF and not (>= 0x01000080 and <= 0x0100009F));

    // whether a layout types Latin letters, from its level-0 keysyms on keycodes minKeycode to maxKeycode
    // (baseOf gives a keycode's)
    internal static bool IsLatinLayout(uint minKeycode, uint maxKeycode, Func<uint, ulong> baseOf)
    {
        var letters = 0;
        for (var keycode = minKeycode; keycode <= maxKeycode; keycode++)
            if (baseOf(keycode) is >= 'a' and <= 'z' && ++letters >= LatinLetterKeys) return true;
        return false;
    }

    // keysym (the key's in the active layout at level, Shift dropped), unless it is printable at level 0 or 1 on a
    // layout that is not Latin: then the key's base in the first Latin layout that has a printable one there, else
    // the us reference's on xkbKeycode, else keysym. an AltGr level (2+) keeps its keysym: Ctrl+AltGr+2 on ru(typo)
    // is Ctrl+@, which a Latin layout's level 0 would lose. baseInLayout gives layout i's level-0 keysym on the key
    // (0 for none), isLatinLayout whether layout i is Latin.
    internal static ulong Resolve(ulong keysym, int activeLayout, int layouts, Func<int, ulong> baseInLayout, Func<int, bool> isLatinLayout, uint xkbKeycode, uint level = 0)
    {
        if (level >= 2 || !IsPrintable(keysym) || isLatinLayout(activeLayout)) return keysym;
        for (var layout = 0; layout < layouts; layout++)
        {
            if (!isLatinLayout(layout)) continue;
            var sym = baseInLayout(layout);
            if (IsPrintable(sym)) return sym;
        }
        var reference = ReferenceBase(xkbKeycode);
        return IsPrintable(reference) ? reference : keysym;
    }

    // the key's level-0 keysym in the us reference keymap; 0 when it has none
    internal static ulong ReferenceBase(uint xkbKeycode)
    {
        var keymap = Reference.Value;
        return keymap is null ? 0 : BaseKeysym(keymap.Handle, xkbKeycode, 0);
    }

    // the level-0 keysym of the key in a layout of an xkbcommon keymap; 0 when it has none
    internal static ulong BaseKeysym(nint keymap, uint xkbKeycode, uint layout)
    {
        var count = EvdevNativeMethods.xkb_keymap_key_get_syms_by_level(keymap, xkbKeycode, layout, 0, out var symsPtr);
        return count > 0 && symsPtr != nint.Zero ? (uint)Marshal.ReadInt32(symsPtr) : 0;
    }

    // the level-0 keysym of the key in a layout it has; 0 past its last one, where xkbcommon would wrap a one-layout
    // key (Escape, or a sparse keymap's letter) onto its group 0 and count that layout's letters for every layout
    internal static ulong OwnBaseKeysym(nint keymap, uint xkbKeycode, uint layout) =>
        layout < EvdevNativeMethods.xkb_keymap_num_layouts_for_key(keymap, xkbKeycode) ? BaseKeysym(keymap, xkbKeycode, layout) : 0;

    // which layouts of an xkbcommon keymap are Latin, by layout index, counting only the keys that have the layout;
    // the master's resolver and the slave's lookup both take this one rule
    internal static bool[] LatinLayouts(nint keymap)
    {
        var min = EvdevNativeMethods.xkb_keymap_min_keycode(keymap);
        var max = EvdevNativeMethods.xkb_keymap_max_keycode(keymap);
        var latin = new bool[EvdevNativeMethods.xkb_keymap_num_layouts(keymap)];
        for (var layout = 0; layout < latin.Length; layout++)
        {
            var l = (uint)layout;
            latin[layout] = IsLatinLayout(min, max, key => OwnBaseKeysym(keymap, key, l));
        }
        return latin;
    }

    // the keysym a shortcut sends from a key under an xkbcommon state (EvdevKeyResolver): its level under only the
    // levelMods (LevelMods: AltGr, level 5, NumLock), so Ctrl+Shift+C sends c as on us (Shift stays in the modifiers)
    // and Ctrl+Alt+F1 sends F1, not the CTRL+ALT type's XF86_Switch_VT_1. at an AltGr or level-5 level that is the
    // layout's keysym there, kept; else the key's base through Resolve. keysym is the state's own, kept where the key
    // has none at that level.
    internal static ulong ForState(nint keymap, nint state, uint xkbKeycode, ulong keysym, Func<int, bool> isLatinLayout, uint levelMods)
    {
        var layout = EvdevNativeMethods.xkb_state_serialize_layout(state, EvdevNativeMethods.XKB_STATE_LAYOUT_EFFECTIVE);
        var mods = EvdevNativeMethods.xkb_state_serialize_mods(state, EvdevNativeMethods.XKB_STATE_MODS_EFFECTIVE) & levelMods;
        var level = Level(keymap, xkbKeycode, layout, mods);
        var sym = KeysymAt(keymap, xkbKeycode, layout, level >= 2 ? level : 0);
        if (sym != 0) keysym = sym;
        var layouts = (int)EvdevNativeMethods.xkb_keymap_num_layouts_for_key(keymap, xkbKeycode);
        return Resolve(keysym, (int)layout, layouts, i => BaseKeysym(keymap, xkbKeycode, (uint)i), isLatinLayout, xkbKeycode, level);
    }

    // the level xkbcommon picks on the key under mods: the type entry whose mask equals mods within the modifiers the
    // entries name, else level 0, as xkb_state_key_get_level does from the type's own mask
    internal static uint Level(nint keymap, uint xkbKeycode, uint layout, uint mods)
    {
        var levels = EvdevNativeMethods.xkb_keymap_num_levels_for_key(keymap, xkbKeycode, layout);
        var masks = new uint[MaxMasks];
        var named = 0u;
        for (var level = 0u; level < levels; level++)
        {
            var count = MasksFor(keymap, xkbKeycode, layout, level, masks);
            for (var i = 0; i < count; i++) named |= masks[i];
        }
        var relevant = mods & named;
        for (var level = 0u; level < levels; level++)
        {
            var count = MasksFor(keymap, xkbKeycode, layout, level, masks);
            for (var i = 0; i < count; i++)
                if (masks[i] == relevant) return level;
        }
        return 0;
    }

    private static int MasksFor(nint keymap, uint xkbKeycode, uint layout, uint level, uint[] masks) =>
        Math.Min((int)EvdevNativeMethods.xkb_keymap_key_get_mods_for_level(keymap, xkbKeycode, layout, level, masks, (nuint)masks.Length), masks.Length);

    private static ulong KeysymAt(nint keymap, uint xkbKeycode, uint layout, uint level)
    {
        var count = EvdevNativeMethods.xkb_keymap_key_get_syms_by_level(keymap, xkbKeycode, layout, level, out var symsPtr);
        return count > 0 && symsPtr != nint.Zero ? (uint)Marshal.ReadInt32(symsPtr) : 0;
    }

    // the real modifiers a shortcut's level may take: Mod5, and whatever LevelThree, LevelFive and NumLock map to.
    // Control, Alt and Super never pick one: symbols/pc's srvr_ctrl(fkey2vt) and keypad(operators) put F1-F12 and
    // the keypad operators on type CTRL+ALT, whose Control+Alt level is XF86_Switch_VT_1 and the like, on every pc105
    // keymap. a virtual modifier's real ones are read back through a scratch state, which xkb_state_update_mask
    // resolves them in, since xkbcommon has no call that names a vmod's mapping
    internal static uint LevelMods(nint keymap)
    {
        var wanted = ModBit(keymap, "Mod5") | ModBit(keymap, "LevelThree") | ModBit(keymap, "LevelFive") | ModBit(keymap, "NumLock");
        var never = ModBit(keymap, "Shift") | ModBit(keymap, "Lock") | ModBit(keymap, "Control") | ModBit(keymap, "Mod1") | ModBit(keymap, "Mod4")
            | ModBit(keymap, "Alt") | ModBit(keymap, "Meta") | ModBit(keymap, "Super") | ModBit(keymap, "Hyper");
        var state = EvdevNativeMethods.xkb_state_new(keymap);
        if (state == 0) return wanted & ~never;
        try
        {
            _ = EvdevNativeMethods.xkb_state_update_mask(state, wanted, 0, 0, 0, 0, 0);
            // the names' own bits kept too, for a keymap whose state keeps a virtual modifier as itself
            return (EvdevNativeMethods.xkb_state_serialize_mods(state, EvdevNativeMethods.XKB_STATE_MODS_DEPRESSED) | wanted) & ~never;
        }
        finally
        {
            EvdevNativeMethods.xkb_state_unref(state);
        }
    }

    private static uint ModBit(nint keymap, string name)
    {
        var index = EvdevNativeMethods.xkb_keymap_mod_get_index(keymap, name);
        return index is EvdevNativeMethods.XKB_MOD_INVALID or >= 32 ? 0 : 1u << (int)index;
    }

    // the evdev key typing the keysym in the us reference keymap, at level 0 or, Shifted, level 1: a shortcut's key on
    // a keymap with no Latin layout, which apps match as the Latin shortcut; null when the reference has none.
    // cached per keysym, misses included, since each lookup scans every key at two levels
    internal static XkbKeyStroke? ReferenceKeyFor(uint keysym) =>
        keysym == 0 ? null : ReferenceKeys.GetOrAdd(keysym, SearchReference);

    // whether ReferenceKeyFor has the keysym's answer cached
    internal static bool IsReferenceKeyCached(uint keysym) => ReferenceKeys.ContainsKey(keysym);

    private static XkbKeyStroke? SearchReference(uint keysym)
    {
        if (Reference.Value is not { } reference) return null;
        var keymap = reference.Handle;
        var min = Math.Max(EvdevNativeMethods.xkb_keymap_min_keycode(keymap), 9u);
        var max = EvdevNativeMethods.xkb_keymap_max_keycode(keymap);
        for (var level = 0u; level < 2; level++)
            for (var key = min; key <= max; key++)
            {
                var count = EvdevNativeMethods.xkb_keymap_key_get_syms_by_level(keymap, key, 0, level, out var symsPtr);
                if (count == 1 && symsPtr != nint.Zero && (uint)Marshal.ReadInt32(symsPtr) == keysym)
                    return new XkbKeyStroke(key - 8, level == 1, false);
            }
        return null;
    }

    private static XkbKeymap? CompileReference()
    {
        try
        {
            return XkbKeymap.FromNames(new LinuxInputConfig.XkbNames("us", "pc105", null, null));
        }
        catch (Exception e) when (e is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            // an X11 master may run without libxkbcommon or its layout data; it keeps the key's own keysym
            return null;
        }
    }
}
