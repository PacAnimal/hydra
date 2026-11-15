using System.Numerics;
using System.Runtime.InteropServices;
using Cathedral.Utils;

namespace Hydra.Platform.Linux;

// the key and modifiers that type a keysym on a keymap; NumLock is the lock a keypad level needs on, Group the
// layout group (xkb layout index) that must be effective for the key to type it
internal sealed record XkbKeyStroke(uint EvdevKey, bool Shift, bool AltGr, bool NumLock = false, uint Group = 0);

// which key a keysym is on and what a key types alone, as a keymap answers it
internal interface IKeymapKeys
{
    uint? EvdevKeyFor(uint keysym);

    uint BaseKeysym(uint evdevKey);
}

// answers which evdev key, with which of Shift, AltGr and NumLock, types a keysym on a keymap given as text
// (XKB_KEYMAP_FORMAT_TEXT_V1, as an EIS server hands it over libei). every layout group is searched, and the stroke
// says which group it needs: typing it under another group types something else, so a caller that cannot select
// the group takes only a stroke in the effective one.
internal sealed class XkbKeymapLookup : IKeymapKeys, IDisposable
{
    private const int MaxMasks = 32;
    private const uint MaxInjectableXkbKey = 255;
    private const uint VoidSymbol = 0xFFFFFF;
    private readonly XkbKeymap _xkb;
    private readonly bool _ownsKeymap;
    private readonly nint _keymap;
    private readonly uint _shiftMask;
    private readonly uint _altGrMask;
    private readonly uint _numLockMask;
    private readonly Lock _lock = new();
    private readonly Toggle _disposed = new();
    // by group << 32 | keysym
    private readonly Dictionary<ulong, XkbKeyStroke?> _cache = [];
    private readonly List<uint> _unbound = [];
    private bool _unboundSearched;
    private uint? _groups;
    private bool[]? _latinGroups;

    internal XkbKeymapLookup(string keymapText)
        : this(XkbKeymap.FromText(keymapText), true)
    {
    }

    // looks up on a keymap the caller owns and disposes after this lookup
    internal XkbKeymapLookup(XkbKeymap keymap)
        : this(keymap, false)
    {
    }

    private XkbKeymapLookup(XkbKeymap keymap, bool ownsKeymap)
    {
        _xkb = keymap;
        _ownsKeymap = ownsKeymap;
        _keymap = keymap.Handle;
        _shiftMask = keymap.ModMask("Shift");
        // AltGr is LevelThree, which keymaps map onto Mod5; level masks may name either
        _altGrMask = keymap.ModMask("Mod5") | keymap.ModMask("LevelThree");
        // NumLock is Mod2 on every stock keymap; level masks may name either
        _numLockMask = keymap.ModMask("Mod2") | keymap.ModMask("NumLock");
        CapsLockMask = keymap.ModMask("Lock");
    }

    // the bits CapsLock and NumLock set in a modifier mask, as the compositor reports locked modifiers
    internal uint CapsLockMask { get; }

    internal uint NumLockMask => _numLockMask;

    // compiles a keymap from RMLVO names and returns it as text
    internal static string KeymapTextFromNames(LinuxInputConfig.XkbNames xkb)
    {
        using var keymap = XkbKeymap.FromNames(xkb);
        return keymap.Text();
    }

    // the keysym for a character; 0 (NoSymbol) for a lone surrogate or a codepoint without one
    internal static uint CharToKeysym(char c) =>
        char.IsSurrogate(c) ? 0 : EvdevNativeMethods.xkb_utf32_to_keysym(c);

    // group 0's key only: its callers (special keys, chords) press no group
    public uint? EvdevKeyFor(uint keysym) => Find(keysym) is { Group: 0 } stroke ? stroke.EvdevKey : null;

    // the first layout group that types Latin letters (LatinShortcutKeysym's rule), whose key a shortcut is pressed on
    // as the desktops match shortcuts; null when no group does
    internal uint? LatinGroup
    {
        get
        {
            lock (_lock)
            {
                if (_disposed) return null;
                var latin = LatinGroups();
                for (var group = 0; group < latin.Length; group++)
                    if (latin[group]) return (uint)group;
                return null;
            }
        }
    }

    // whether the group types Latin letters: a shortcut there is pressed on its own key, as a desktop on that layout
    // matches Ctrl+z on AZERTY's or QWERTZ's z key
    internal bool IsLatinGroup(uint group)
    {
        lock (_lock)
        {
            if (_disposed) return false;
            var latin = LatinGroups();
            return group < latin.Length && latin[group];
        }
    }

    // under _lock; by group, computed once
    private bool[] LatinGroups()
    {
        if (_latinGroups is { } known) return known;
        var min = EvdevNativeMethods.xkb_keymap_min_keycode(_keymap);
        var max = EvdevNativeMethods.xkb_keymap_max_keycode(_keymap);
        var latin = new bool[GroupCount()];
        for (var group = 0u; group < latin.Length; group++)
        {
            var layout = group;
            latin[group] = LatinShortcutKeysym.IsLatinLayout(min, max, key => layout < EvdevNativeMethods.xkb_keymap_num_layouts_for_key(_keymap, key)
                ? LatinShortcutKeysym.BaseKeysym(_keymap, key, layout)
                : 0);
        }
        _latinGroups = latin;
        return latin;
    }

    // the lowest level that types the keysym with at most Shift, AltGr and NumLock, in the preferred group (the
    // slave's effective one) if it has one, else group 0, else the others in order; null if no group does
    internal XkbKeyStroke? Find(uint keysym, uint preferredGroup = 0)
    {
        if (keysym == 0) return null;
        lock (_lock)
        {
            if (_disposed) return null;
            var groups = GroupCount();
            if (preferredGroup < groups && Cached(keysym, preferredGroup) is { } preferred) return preferred;
            for (var group = 0u; group < groups; group++)
                if (group != preferredGroup && Cached(keysym, group) is { } stroke) return stroke;
            return null;
        }
    }

    // under _lock
    private XkbKeyStroke? Cached(uint keysym, uint group)
    {
        var key = ((ulong)group << 32) | keysym;
        if (_cache.TryGetValue(key, out var cached)) return cached;
        var stroke = Search(keysym, group);
        _cache[key] = stroke;
        return stroke;
    }

    // under _lock; the most layouts any key has
    private uint GroupCount()
    {
        if (_groups is { } known) return known;
        var min = EvdevNativeMethods.xkb_keymap_min_keycode(_keymap);
        var max = EvdevNativeMethods.xkb_keymap_max_keycode(_keymap);
        var groups = 0u;
        for (var key = min; key <= max; key++)
            groups = Math.Max(groups, EvdevNativeMethods.xkb_keymap_num_layouts_for_key(_keymap, key));
        _groups = groups;
        return groups;
    }

    // the key's layout under the group: a key with one layout (Escape, the keypad) types it in every group; one
    // with fewer than the group needs is skipped, since how it maps an out-of-range group is the keymap's choice
    private uint? KeyLayout(uint key, uint group)
    {
        var layouts = EvdevNativeMethods.xkb_keymap_num_layouts_for_key(_keymap, key);
        if (layouts == 0) return null;
        if (group < layouts) return group;
        return layouts == 1 ? 0 : null;
    }

    private XkbKeyStroke? Search(uint keysym, uint group)
    {
        var min = EvdevNativeMethods.xkb_keymap_min_keycode(_keymap);
        var max = EvdevNativeMethods.xkb_keymap_max_keycode(_keymap);
        var maxLevels = 0u;
        for (var key = min; key <= max; key++)
            if (KeyLayout(key, group) is { } layout)
                maxLevels = Math.Max(maxLevels, EvdevNativeMethods.xkb_keymap_num_levels_for_key(_keymap, key, layout));

        var masks = new uint[MaxMasks];
        // level outermost so a lower level on any key wins over a higher one
        for (var level = 0u; level < maxLevels; level++)
        {
            for (var key = min; key <= max; key++)
            {
                // evdev keycode + 8 = xkb keycode; keys below 8 have no evdev code
                if (key < 8) continue;
                if (KeyLayout(key, group) is not { } layout) continue;
                if (level >= EvdevNativeMethods.xkb_keymap_num_levels_for_key(_keymap, key, layout)) continue;
                if (!LevelTypes(key, layout, level, keysym)) continue;
                var mask = LevelMask(key, layout, level, masks);
                if (mask is not { } m) continue;
                return new XkbKeyStroke(key - 8, (m & _shiftMask) != 0, (m & _altGrMask) != 0, (m & _numLockMask) != 0, group);
            }
        }
        return null;
    }

    // a level types the keysym only when it is that level's single keysym
    private bool LevelTypes(uint key, uint layout, uint level, uint keysym)
    {
        var count = EvdevNativeMethods.xkb_keymap_key_get_syms_by_level(_keymap, key, layout, level, out var symsPtr);
        return count == 1 && symsPtr != nint.Zero && (uint)Marshal.ReadInt32(symsPtr) == keysym;
    }

    // the keysym an evdev key types at level 0 of group 0; 0 (NoSymbol) when it types none
    public uint BaseKeysym(uint evdevKey)
    {
        lock (_lock) return _disposed ? 0 : Level0Keysym(evdevKey + 8);
    }

    // an evdev key that types nothing (NoSymbol or VoidSymbol), the highest first so a key a real keyboard has is the
    // last resort; null when every key types something. only xkb keycodes up to 255 (evdev 247), the X11 range: every
    // compositor's virtual keyboard takes those, while mutter drops keys past KEY_KBDINPUTASSIST_CANCEL (0x26e)
    internal uint? UnboundKey() => UnboundKeys(1);

    // an unbound key below UnboundKey that types nothing at any level, free for a keysym bound on a keymap of our own
    // while UnboundKey stays the key that types nothing; null when the keymap has none
    internal uint? SpareKey() => UnboundKeys(2);

    // the count'th unbound key, highest first: the first empty at level 0, the second at every level
    private uint? UnboundKeys(int count)
    {
        lock (_lock)
        {
            if (_disposed) return null;
            if (!_unboundSearched)
            {
                _unboundSearched = true;
                // evdev keycode + 8 = xkb keycode; evdev's 0 is KEY_RESERVED, no key
                var min = Math.Max(EvdevNativeMethods.xkb_keymap_min_keycode(_keymap), 9u);
                var max = Math.Min(EvdevNativeMethods.xkb_keymap_max_keycode(_keymap), MaxInjectableXkbKey);
                for (var key = max; key >= min && _unbound.Count < 2; key--)
                    if (_unbound.Count == 0 ? Level0Keysym(key) is 0 or VoidSymbol : TypesNothing(key)) _unbound.Add(key - 8);
            }
            return _unbound.Count >= count ? _unbound[count - 1] : null;
        }
    }

    // under _lock
    private bool TypesNothing(uint key)
    {
        if (EvdevNativeMethods.xkb_keymap_num_layouts_for_key(_keymap, key) == 0) return true;
        var levels = EvdevNativeMethods.xkb_keymap_num_levels_for_key(_keymap, key, 0);
        for (var level = 0u; level < levels; level++)
        {
            var count = EvdevNativeMethods.xkb_keymap_key_get_syms_by_level(_keymap, key, 0, level, out var symsPtr);
            for (var i = 0; i < count && symsPtr != nint.Zero; i++)
                if ((uint)Marshal.ReadInt32(symsPtr, i * 4) is not (0 or VoidSymbol)) return false;
        }
        return true;
    }

    // under _lock
    private uint Level0Keysym(uint key)
    {
        if (EvdevNativeMethods.xkb_keymap_num_layouts_for_key(_keymap, key) == 0) return 0;
        var count = EvdevNativeMethods.xkb_keymap_key_get_syms_by_level(_keymap, key, 0, 0, out var symsPtr);
        return count >= 1 && symsPtr != nint.Zero ? (uint)Marshal.ReadInt32(symsPtr) : 0;
    }

    // the mask with the fewest modifiers that selects the level using only Shift, AltGr and NumLock; null if none does
    private uint? LevelMask(uint key, uint layout, uint level, uint[] masks)
    {
        var count = (int)EvdevNativeMethods.xkb_keymap_key_get_mods_for_level(_keymap, key, layout, level, masks, (nuint)masks.Length);
        var allowed = _shiftMask | _altGrMask | _numLockMask;
        uint? best = null;
        for (var i = 0; i < count && i < masks.Length; i++)
        {
            var mask = masks[i];
            if ((mask & ~allowed) != 0) continue;
            if (best is not { } b || BitOperations.PopCount(mask) < BitOperations.PopCount(b))
                best = mask;
        }
        return best;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (!_disposed.TrySet()) return;
            if (_ownsKeymap) _xkb.Dispose();
        }
    }
}
