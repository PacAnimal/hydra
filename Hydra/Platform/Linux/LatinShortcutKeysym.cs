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

    // keysym (the key's base in the active layout), unless it is printable on a layout that is not Latin: then the
    // key's base in the first Latin layout that has a printable one there, else the us reference's on xkbKeycode,
    // else keysym. baseInLayout gives layout i's level-0 keysym on the key (0 for none), isLatinLayout whether
    // layout i is Latin.
    internal static ulong Resolve(ulong keysym, int activeLayout, int layouts, Func<int, ulong> baseInLayout, Func<int, bool> isLatinLayout, uint xkbKeycode)
    {
        if (!IsPrintable(keysym) || isLatinLayout(activeLayout)) return keysym;
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

    // which layouts of an xkbcommon keymap are Latin, by layout index
    internal static bool[] LatinLayouts(nint keymap)
    {
        var min = EvdevNativeMethods.xkb_keymap_min_keycode(keymap);
        var max = EvdevNativeMethods.xkb_keymap_max_keycode(keymap);
        var latin = new bool[EvdevNativeMethods.xkb_keymap_num_layouts(keymap)];
        for (var layout = 0; layout < latin.Length; layout++)
        {
            var l = (uint)layout;
            latin[layout] = IsLatinLayout(min, max, key => BaseKeysym(keymap, key, l));
        }
        return latin;
    }

    // the evdev key typing the keysym in the us reference keymap, at level 0 or, Shifted, level 1: a shortcut's key on
    // a keymap with no Latin layout, which apps match as the Latin shortcut; null when the reference has none
    internal static XkbKeyStroke? ReferenceKeyFor(uint keysym)
    {
        if (keysym == 0 || Reference.Value is not { } reference) return null;
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
