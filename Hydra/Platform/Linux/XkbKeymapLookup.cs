using System.Numerics;
using System.Runtime.InteropServices;
using Cathedral.Utils;

namespace Hydra.Platform.Linux;

// the key and modifiers that type a keysym on a keymap; NumLock is the lock a keypad level needs on
internal sealed record XkbKeyStroke(uint EvdevKey, bool Shift, bool AltGr, bool NumLock = false);

// answers which evdev key, with which of Shift, AltGr and NumLock, types a keysym on a keymap given as text
// (XKB_KEYMAP_FORMAT_TEXT_V1, as an EIS server hands it over libei). only layout group 0 is searched:
// a stroke carries no group, so a key found in another group would type something else.
internal sealed class XkbKeymapLookup : IDisposable
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
    private readonly Dictionary<uint, XkbKeyStroke?> _cache = [];
    private readonly List<uint> _unbound = [];
    private bool _unboundSearched;

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

    internal uint? EvdevKeyFor(uint keysym) => Find(keysym)?.EvdevKey;

    // the lowest level of group 0 that types the keysym with at most Shift, AltGr and NumLock; null if none
    internal XkbKeyStroke? Find(uint keysym)
    {
        if (keysym == 0) return null;
        lock (_lock)
        {
            if (_disposed) return null;
            if (_cache.TryGetValue(keysym, out var cached)) return cached;
            var stroke = Search(keysym);
            _cache[keysym] = stroke;
            return stroke;
        }
    }

    private XkbKeyStroke? Search(uint keysym)
    {
        var min = EvdevNativeMethods.xkb_keymap_min_keycode(_keymap);
        var max = EvdevNativeMethods.xkb_keymap_max_keycode(_keymap);
        var maxLevels = 0u;
        for (var key = min; key <= max; key++)
            if (EvdevNativeMethods.xkb_keymap_num_layouts_for_key(_keymap, key) > 0)
                maxLevels = Math.Max(maxLevels, EvdevNativeMethods.xkb_keymap_num_levels_for_key(_keymap, key, 0));

        var masks = new uint[MaxMasks];
        // level outermost so a lower level on any key wins over a higher one
        for (var level = 0u; level < maxLevels; level++)
        {
            for (var key = min; key <= max; key++)
            {
                // evdev keycode + 8 = xkb keycode; keys below 8 have no evdev code
                if (key < 8) continue;
                if (EvdevNativeMethods.xkb_keymap_num_layouts_for_key(_keymap, key) == 0) continue;
                if (level >= EvdevNativeMethods.xkb_keymap_num_levels_for_key(_keymap, key, 0)) continue;
                if (!LevelTypes(key, level, keysym)) continue;
                var mask = LevelMask(key, level, masks);
                if (mask is not { } m) continue;
                return new XkbKeyStroke(key - 8, (m & _shiftMask) != 0, (m & _altGrMask) != 0, (m & _numLockMask) != 0);
            }
        }
        return null;
    }

    // a level types the keysym only when it is that level's single keysym
    private bool LevelTypes(uint key, uint level, uint keysym)
    {
        var count = EvdevNativeMethods.xkb_keymap_key_get_syms_by_level(_keymap, key, 0, level, out var symsPtr);
        return count == 1 && symsPtr != nint.Zero && (uint)Marshal.ReadInt32(symsPtr) == keysym;
    }

    // the keysym an evdev key types at level 0 of group 0; 0 (NoSymbol) when it types none
    internal uint BaseKeysym(uint evdevKey)
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
    private uint? LevelMask(uint key, uint level, uint[] masks)
    {
        var count = (int)EvdevNativeMethods.xkb_keymap_key_get_mods_for_level(_keymap, key, 0, level, masks, (nuint)masks.Length);
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
