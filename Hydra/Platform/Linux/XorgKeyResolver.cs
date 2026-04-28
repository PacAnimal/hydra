using System.Runtime.InteropServices;
using Hydra.Keyboard;

namespace Hydra.Platform.Linux;

// translates X11 key events into platform-independent KeyEvents.
// resolves the layout-translated character using the active keyboard layout and modifier state,
// matching the Mac approach of using the server-side layout to determine the intended character.
// instance-based to maintain dead key composition state across events.
internal sealed class XorgKeyResolver
{
    private char _pendingDeadKey;
    private char _pendingDeadSpacing;  // spacing form used when composition fails (e.g. dead_tilde + space → ~)
    private readonly Dictionary<uint, CharClassification> _keyDownId = [];
    // tracks hold count per modifier bit to avoid dropping ShiftMask when one of two held shifts releases
    private readonly Dictionary<uint, int> _heldModifierCounts = [];
    // ScrollLock modifier mask — detected at startup via XGetModifierMapping; defaults to Mod3 (standard)
    private uint _scrollLockMask = NativeMethods.Mod3Mask;

    internal KeyEvent?[]? Resolve(int evType, uint keycode, uint state, nint display)
    {
        // resolve using the active layout: group from Xkb state bits, level from Shift/AltGr
        var group = ExtractGroup(state);
        var level = ComputeLevel(state);
        var keysym = NativeMethods.XkbKeycodeToKeysym(display, keycode, group, level);

        // fall back to base keysym (0,0) for modifier keys and unmapped levels
        if (keysym == 0)
            keysym = NativeMethods.XkbKeycodeToKeysym(display, keycode, 0, 0);
        if (keysym == 0) return null;

        // keypad dual-purpose keys: numlock determines whether to produce digits or navigation.
        // ComputeLevel ignores numlock, so we post-correct the keysym here.
        var numLock = (state & NativeMethods.Mod2Mask) != 0;
        if (numLock)
        {
            // numlock on: if we got a navigation keysym (level=0), re-query at level=1 for numeric
            if (keysym is >= 0xFF95 and <= 0xFF9F)
            {
                var sym2 = NativeMethods.XkbKeycodeToKeysym(display, keycode, group, 1);
                if (sym2 != 0) keysym = sym2;
            }

            keysym = KpNumericToChar(keysym);
        }
        else
        {
            // numlock off: if shift gave a numeric keysym (X11 XOR), re-query at level=0 for navigation
            if (level != 0 && (keysym is >= 0xFFB0 and <= 0xFFB9 || keysym is 0xFFAE or 0xFFAC))
            {
                var sym2 = NativeMethods.XkbKeycodeToKeysym(display, keycode, group, 0);
                if (sym2 is >= 0xFF95 and <= 0xFF9F) keysym = sym2;
            }
            // map KP navigation keysyms to their standard counterparts so the slave's numlock state doesn't matter
            keysym = MapKpNavToStandard(keysym);
        }

        // X11 state lags by one: the modifier being pressed/released is not yet reflected.
        // adjust so modifiers always describe what's held after the event takes effect.
        var adjustedState = AdjustModifierState(evType, keysym, state);
        var mods = MapModifiers(adjustedState);
        var type = evType == NativeMethods.KeyPress ? KeyEventType.KeyDown : KeyEventType.KeyUp;

        // key-up: replay the same event that was emitted on key-down
        if (evType == NativeMethods.KeyRelease)
            return [KeyResolver.ReplayKeyUp(_keyDownId, keycode, mods)];

        // suppress auto-repeat: if keycode already in _keyDownId, this is an OS repeat — drop it
        if (_keyDownId.ContainsKey(keycode)) return null;

        // shortcut context (Ctrl/Super held): if the base key is a dead key, clear any pending dead
        // state and emit the spacing form (e.g. Ctrl+` → `) so the shortcut fires with the correct base char.
        // dead keys with no spacing form (e.g. XK_dead_belowdot) are dropped to avoid producing garbage.
        var isShortcut = (state & (NativeMethods.Mod4Mask | NativeMethods.ControlMask)) != 0;
        if (isShortcut && DeadKeyLookup(keysym) is { Combining: not '\0' } deadShortcut)
        {
            // check spacing form before clearing pending state — a dead key with no spacing form (e.g.
            // XK_dead_belowdot on Vietnamese layout) should not discard an existing pending dead key.
            if (deadShortcut.Spacing == '\0') return null;
            // flush any prior pending dead key: dead_grave pending + Ctrl+dead_acute → ` then Ctrl+´
            var prevFlush = TakeDeadKeySpacing(ref _pendingDeadKey, ref _pendingDeadSpacing);
            _keyDownId[keycode] = new CharClassification(deadShortcut.Spacing, null);
            var shortcutEvent = KeyEvent.Char(KeyEventType.KeyDown, deadShortcut.Spacing, mods);
            return prevFlush is not null ? [.. prevFlush, shortcutEvent] : [shortcutEvent];
        }

        // flush pending dead key before any shortcut character (mirrors Windows/Mac behaviour).
        // the shortcut-dead-key branch above handles Ctrl/Super+dead_key; this handles Ctrl/Super+normal_char
        // (e.g. dead_grave + Ctrl+A → '`' then Ctrl+A, not 'à' with Control).
        var shortcutFlush = (isShortcut && _pendingDeadKey != '\0')
            ? TakeDeadKeySpacing(ref _pendingDeadKey, ref _pendingDeadSpacing) : null;

        // pre-flush: non-modifier special key while dead key pending — emit spacing form before the special key.
        // this mirrors Windows' FlushPendingDeadKey: dead_acute + Tab → '´' then Tab, not just Tab.
        var deadFlush = FlushDeadKeyBeforeSpecial(keysym, ref _pendingDeadKey, ref _pendingDeadSpacing);

        // trackDeadKey: register dead keys in _keyDownId so OS auto-repeat is suppressed
        var ev = ResolveKeysym(keysym, keycode, _keyDownId, ref _pendingDeadKey, ref _pendingDeadSpacing, mods, type, trackDeadKey: true);
        // shortcutFlush ?? deadFlush: if shortcutFlush fired it already cleared _pendingDeadKey, so
        // FlushDeadKeyBeforeSpecial (above) would have seen '\0' and returned null — ?? never resolves to deadFlush.
        var flush = shortcutFlush ?? deadFlush;
        if (flush is not null && ev is not null) return [.. flush, .. ev];
        if (flush is not null) return flush;
        return ev;
    }

    // flushes a pending dead key as its spacing form when a non-modifier special key interrupts composition.
    // dead keys with no spacing form (e.g. dead_belowdot) are dropped — never emit the raw combining char.
    // modifiers are always None: the spacing form belongs to the dead key press, not the aborting key.
    // returns [down, up] pair, or null if no flush is needed.
    internal static KeyEvent?[]? FlushDeadKeyBeforeSpecial(ulong keysym, ref char pendingDeadKey, ref char pendingDeadSpacing)
    {
        if (pendingDeadKey == '\0') return null;
        var possibleSpecial = KeySymToSpecialKey(keysym);
        if (!possibleSpecial.HasValue || ModifierBit(keysym) != 0) return null;
        return TakeDeadKeySpacing(ref pendingDeadKey, ref pendingDeadSpacing);
    }

    // unconditionally takes the pending dead key spacing form; clears pending state regardless.
    // used when a shortcut or numpad key interrupts composition and pending state must always be flushed.
    // returns [down, up] pair so the slave does not get a stuck key from the synthetic press.
    internal static KeyEvent?[]? TakeDeadKeySpacing(ref char pendingDeadKey, ref char pendingDeadSpacing)
    {
        if (pendingDeadKey == '\0') return null;
        var spacing = pendingDeadSpacing;
        pendingDeadKey = '\0';
        pendingDeadSpacing = '\0';
        if (spacing == '\0') return null;  // no spacing form — drop silently
        return [
            KeyEvent.Char(KeyEventType.KeyDown, spacing, KeyModifiers.None),
            KeyEvent.Char(KeyEventType.KeyUp, spacing, KeyModifiers.None),
        ];
    }

    // shared dead-key → special → char resolution pipeline, used by XorgKeyResolver and EvdevKeyResolver.
    // trackDeadKey: evdev needs a placeholder _keyDownId entry for dead keys to support key-up replay.
    internal static KeyEvent?[]? ResolveKeysym(
        ulong keysym, uint keycode, Dictionary<uint, CharClassification> keyDownId,
        ref char pendingDeadKey, ref char pendingDeadSpacing,
        KeyModifiers mods, KeyEventType type, bool trackDeadKey = false)
    {
        // dead key: store combining char and spacing form, then wait for the next character.
        // dead + dead: emit spacing form of the first dead key before starting the second.
        var dead = DeadKeyLookup(keysym);
        if (dead.Combining != '\0')
        {
            if (pendingDeadKey != '\0')
            {
                var spc = pendingDeadSpacing;
                pendingDeadKey = dead.Combining;
                pendingDeadSpacing = dead.Spacing;
                if (trackDeadKey) keyDownId[keycode] = new CharClassification(null, null);
                if (spc == '\0') return null;
                // do NOT overwrite the (null,null) placeholder: this key's release should not
                // replay the spacing form — the flush is a synthetic event, not a character produced by this key.
                // emit [down, up] pair: the flush has no physical key-up coming, so the slave must not get a stuck key.
                return [
                    KeyEvent.Char(type, spc, KeyModifiers.None),
                    KeyEvent.Char(KeyEventType.KeyUp, spc, KeyModifiers.None),
                ];
            }
            pendingDeadKey = dead.Combining;
            pendingDeadSpacing = dead.Spacing;
            if (trackDeadKey) keyDownId[keycode] = new CharClassification(null, null);
            return null;
        }

        // special key (arrows, function keys, modifiers, keypad)?
        var special = KeySymToSpecialKey(keysym);
        if (special.HasValue)
        {
            // modifier keys are transparent to dead key composition — don't clear pending state
            if (ModifierBit(keysym) == 0)
            {
                pendingDeadKey = '\0';
                pendingDeadSpacing = '\0';
            }
            keyDownId[keycode] = new CharClassification(null, special.Value);
            return [KeyEvent.Special(type, special.Value, mods)];
        }

        // character key
        var ch = KeySymToChar(keysym);
        if (ch.HasValue)
        {
            if (pendingDeadKey != '\0')
            {
                var pd = pendingDeadKey;
                var spc = pendingDeadSpacing;
                pendingDeadKey = '\0';
                pendingDeadSpacing = '\0';
                ch = KeyResolver.ComposeOrSpacing(ch.Value, pd, spc);
            }
            keyDownId[keycode] = new CharClassification(ch, null);
            return [KeyEvent.Char(type, ch.Value, mods)];
        }

        return null;
    }

    // maps a dead keysym to its combining char and spacing form.
    // spacing is '\0' for dead keys with no natural standalone character.
    // when dead key + space is pressed, spacing form is emitted if available, otherwise space passes through.
    internal static DeadKeyInfo DeadKeyLookup(ulong keysym) => keysym switch
    {
        0xFE50 => new('\u0300', '\u0060'),  // XK_dead_grave              → combining grave, ` spacing
        0xFE51 => new('\u0301', '\u00B4'),  // XK_dead_acute              → combining acute, ´ spacing
        0xFE52 => new('\u0302', '\u005E'),  // XK_dead_circumflex         → combining circumflex, ^ spacing
        0xFE53 => new('\u0303', '\u007E'),  // XK_dead_tilde              → combining tilde, ~ spacing
        0xFE54 => new('\u0304', '\u00AF'),  // XK_dead_macron             → combining macron, ¯ spacing
        0xFE55 => new('\u0306', '\u02D8'),  // XK_dead_breve              → combining breve, ˘ spacing
        0xFE56 => new('\u0307', '\u02D9'),  // XK_dead_abovedot           → combining dot above, ˙ spacing
        0xFE57 => new('\u0308', '\u00A8'),  // XK_dead_diaeresis          → combining diaeresis, ¨ spacing
        0xFE58 => new('\u030A', '\u02DA'),  // XK_dead_abovering          → combining ring above, ˚ spacing
        0xFE59 => new('\u030B', '\u02DD'),  // XK_dead_doubleacute        → combining double acute, ˝ spacing
        0xFE5A => new('\u030C', '\u02C7'),  // XK_dead_caron              → combining caron, ˇ spacing
        0xFE5B => new('\u0327', '\u00B8'),  // XK_dead_cedilla            → combining cedilla, ¸ spacing
        0xFE5C => new('\u0328', '\u02DB'),  // XK_dead_ogonek             → combining ogonek, ˛ spacing
        0xFE5D => new('\u0345', '\u037A'),  // XK_dead_iota               → combining ypogegrammeni (polytonic Greek)
        0xFE60 => new('\u0323', '\0'),      // XK_dead_belowdot           → combining dot below (Vietnamese)
        0xFE61 => new('\u0309', '\0'),      // XK_dead_hook               → combining hook above (Vietnamese)
        0xFE62 => new('\u031B', '\0'),      // XK_dead_horn               → combining horn (Vietnamese)
        0xFE63 => new('\u0335', '\u002F'),  // XK_dead_stroke             → combining short stroke overlay, / spacing
        0xFE64 => new('\u0313', '\u1FBD'),  // XK_dead_abovecomma/psili   → combining comma above (polytonic Greek)
        0xFE65 => new('\u0314', '\u1FFE'),  // XK_dead_abovereversedcomma/dasia → combining reversed comma above
        0xFE66 => new('\u030F', '\0'),      // XK_dead_doublegrave        → combining double grave accent
        0xFE67 => new('\u0325', '\0'),      // XK_dead_belowring          → combining ring below
        0xFE68 => new('\u0331', '\0'),      // XK_dead_belowmacron        → combining macron below
        0xFE69 => new('\u032D', '\0'),      // XK_dead_belowcircumflex    → combining circumflex accent below
        0xFE6A => new('\u0330', '\0'),      // XK_dead_belowtilde         → combining tilde below
        0xFE6B => new('\u032E', '\0'),      // XK_dead_belowbreve         → combining breve below
        0xFE6C => new('\u0324', '\0'),      // XK_dead_belowdiaeresis     → combining diaeresis below
        0xFE6D => new('\u0311', '\0'),      // XK_dead_invertedbreve      → combining inverted breve
        0xFE6E => new('\u0326', '\0'),      // XK_dead_belowcomma         → combining comma below (Romanian)
        _ => new('\0', '\0'),
    };

    // checks special key map first, then falls back to mechanical MISCELLANY mapping.
    // MISCELLANY keysyms (0xFF00-0xFFFF) map directly: keysym | 0x01000000 = SpecialKey value.
    internal static SpecialKey? KeySymToSpecialKey(ulong keysym)
    {
        if (XorgSpecialKeyMap.Instance.TryGet(keysym, out var special)) return special;

        // mechanical MISCELLANY mapping for named keys not in the special map
        if (keysym is >= 0xFF00 and <= 0xFFFF)
            return (SpecialKey)(keysym | 0x01000000);

        return null;
    }

    // maps a keysym to a printable char.
    // latin-1 printable range (0x0020-0x00FF) maps directly as unicode codepoints.
    // modern Unicode-extension keysyms (0x01000000 | codepoint) strip the flag bit.
    // legacy named keysyms (latin 2-4, greek, cyrillic, arabic, etc.) use s_legacyKeysymMap.
    // remaining keysyms in 0x0100-0xFDFF where keysym == unicode (e.g. EuroSign = 0x20AC) fall through.
    internal static char? KeySymToChar(ulong keysym)
    {
        if (keysym is >= 0x0020 and <= 0x00FF)
            return (char)keysym;

        // unicode-extension keysyms: strip 0x01000000 flag, BMP only, excluding surrogates
        if (keysym is >= 0x01000100 and <= 0x0100FFFF and not (>= 0x0100D800 and <= 0x0100DFFF))
            return (char)(keysym - 0x01000000);

        // legacy named keysyms where X11 keysym value != unicode codepoint
        if (LegacyKeysymMap.TryGetValue(keysym, out var mapped))
            return mapped;

        // 0xFE00-0xFEFF is the X11 dead-key / XFree86 extension range — not Unicode codepoints.
        // unlisted dead keysyms in this range (e.g. XK_dead_currency 0xFE6F) would otherwise be
        // returned as garbage characters rather than being handled by DeadKeyLookup or dropped.
        // for any legacy script range NOT in LegacyKeysymMap, this cast also produces garbage —
        // such scripts should either have map entries above or use the 0x01000000-extension format.
        if (keysym is > 0x00FF and < 0xFE00)
            return (char)keysym;

        return null;
    }

    // Xkb group is encoded in bits 13-14 of the X11 state field (XkbGroupForCoreState macro)
    private static int ExtractGroup(uint state) => (int)((state >> 13) & 3);

    // shift level: 0=base, 1=Shift, 2=AltGr, 3=Shift+AltGr.
    // when Super or Control is held (shortcut context), Shift is stripped from level so the base
    // character is resolved (e.g. '4' not '¤' on Norwegian for Super+Shift+4). Shift remains in mods.
    private static int ComputeLevel(uint state)
    {
        var isShortcut = (state & (NativeMethods.Mod4Mask | NativeMethods.ControlMask)) != 0;
        var shift = !isShortcut && (state & NativeMethods.ShiftMask) != 0;
        var altGr = (state & NativeMethods.Mod5Mask) != 0;
        return (shift ? 1 : 0) + (altGr ? 2 : 0);
    }

    // X11 state field reflects modifier state before the event takes effect.
    // for modifier key presses: the bit isn't set yet — add it.
    // for modifier key releases: the bit is still set — remove it.
    // tracks per-bit hold counts so releasing one of two held shifts doesn't drop ShiftMask prematurely.
    private uint AdjustModifierState(int evType, ulong keysym, uint state)
    {
        var bit = ModifierBit(keysym);
        if (bit == 0) return state;
        _heldModifierCounts.TryGetValue(bit, out var count);
        if (evType == NativeMethods.KeyPress)
        {
            _heldModifierCounts[bit] = count + 1;
            return state | bit;
        }
        _heldModifierCounts[bit] = Math.Max(0, count - 1);
        // only clear the bit when we tracked a matching press (count > 0 before decrement).
        // if count was 0, the modifier was held before tracking started — preserve the state bit
        // rather than clearing it prematurely; the next non-modifier event will have the correct state.
        return (count > 0 && _heldModifierCounts[bit] == 0) ? state & ~bit : state;
    }

    internal record DeadKeyInfo(char Combining, char Spacing);

    // X11 modifier mask bit for a modifier keysym, or 0 for non-modifier keys
    internal static uint ModifierBit(ulong keysym) => keysym switch
    {
        XorgVirtualKey.Shift_L or XorgVirtualKey.Shift_R => NativeMethods.ShiftMask,
        XorgVirtualKey.Control_L or XorgVirtualKey.Control_R => NativeMethods.ControlMask,
        XorgVirtualKey.Alt_L or XorgVirtualKey.Alt_R => NativeMethods.Mod1Mask,
        XorgVirtualKey.Super_L or XorgVirtualKey.Super_R => NativeMethods.Mod4Mask,
        XorgVirtualKey.CapsLock => NativeMethods.LockMask,
        XorgVirtualKey.NumLock => NativeMethods.Mod2Mask,
        XorgVirtualKey.ISO_Level3_Shift or 0xFF7EUL => NativeMethods.Mod5Mask,  // ISO_Level3_Shift or Mode_switch
        _ => 0,
    };

    // maps KP navigation keysyms (0xFF95-0xFF9F, numlock-off) to their standard navigation equivalents.
    // this decouples the slave from needing its own numlock off to produce navigation behavior.
    // KP_Begin (0xFF9D, center 5) has no standard nav equivalent and passes through.
    internal static ulong MapKpNavToStandard(ulong keysym) => keysym switch
    {
        0xFF95 => 0xFF50,  // KP_Home → Home
        0xFF96 => 0xFF51,  // KP_Left → Left
        0xFF97 => 0xFF52,  // KP_Up → Up
        0xFF98 => 0xFF53,  // KP_Right → Right
        0xFF99 => 0xFF54,  // KP_Down → Down
        0xFF9A => 0xFF55,  // KP_Prior → PageUp
        0xFF9B => 0xFF56,  // KP_Next → PageDown
        0xFF9C => 0xFF57,  // KP_End → End
        0xFF9E => 0xFF63,  // KP_Insert → Insert
        0xFF9F => 0xFFFF,  // KP_Delete → Delete
        _ => keysym,
    };

    // maps KP navigation keysyms to their digit/decimal chars (for numlock-on handling in evdev).
    // the standard numpad layout: 7=Home, 8=Up, 9=PgUp, 4=Left, 5=Begin, 6=Right, 1=End, 2=Down, 3=PgDn, 0=Insert, .=Delete
    internal static ulong KpNavToChar(ulong keysym) => keysym switch
    {
        0xFF9E => '0',
        0xFF9C => '1',
        0xFF99 => '2',
        0xFF9B => '3',
        0xFF96 => '4',
        0xFF9D => '5',
        0xFF98 => '6',
        0xFF95 => '7',
        0xFF97 => '8',
        0xFF9A => '9',
        0xFF9F => '.',
        _ => keysym,
    };

    // converts KP numeric keysyms (digits, decimal, separator) to their Unicode char equivalents,
    // so the slave can emit them without caring about its own numlock state.
    // covers: KP_0–KP_9 (0xFFB0–0xFFB9), KP_Decimal (.), KP_Separator/comma (0xFFAC).
    internal static ulong KpNumericToChar(ulong keysym) => keysym switch
    {
        >= 0xFFB0 and <= 0xFFB9 => '0' + (keysym - 0xFFB0),
        XorgVirtualKey.KP_Decimal => '.',
        0xFFAC => ',',  // XK_KP_Separator — comma on European layouts
        _ => keysym,
    };

    // maps KP numeric keysyms to their KP navigation equivalents (for numlock-off handling in evdev).
    internal static ulong KpNumericToNav(ulong keysym) => keysym switch
    {
        0xFFB0 => 0xFF9E,
        0xFFB1 => 0xFF9C,
        0xFFB2 => 0xFF99,
        0xFFB3 => 0xFF9B,
        0xFFB4 => 0xFF96,
        0xFFB5 => 0xFF9D,
        0xFFB6 => 0xFF98,
        0xFFB7 => 0xFF95,
        0xFFB8 => 0xFF97,
        0xFFB9 => 0xFF9A,
        XorgVirtualKey.KP_Decimal or 0xFFAC => 0xFF9F,  // KP_Decimal / KP_Separator → KP_Delete
        _ => keysym,
    };

    // legacy X11 keysyms where the keysym value is NOT equal to the Unicode codepoint.
    // covers latin 2-4 (0x01xx-0x03xx), katakana (0x04xx), arabic (0x05xx), cyrillic (0x06xx),
    // greek (0x07xx), technical (0x08xx), special (0x09xx), publishing (0x0Axx), APL (0x0Bxx),
    // hebrew (0x0Cxx), thai (0x0Dxx), hangul (0x0Exx), latin 8 / Celtic (0x12xx), and latin 9 extras (0x13xx).
    // excludes entries where keysym == unicode (those are handled by the raw-cast fallback).
    // excludes the 0x01000000+ unicode-extension range (handled by the flag-strip case above).
    // source: X11 keysymdef.h + XKBUtil.cpp from input-leap.
    private static readonly Dictionary<ulong, char> LegacyKeysymMap = new()
    {
        // latin 2 (XK_LATIN2, byte 3 = 1)
        { 0x01A1, '\u0104' },  // XK_Aogonek        → Ą
        { 0x01A2, '\u02D8' },  // XK_breve          → ˘
        { 0x01A3, '\u0141' },  // XK_Lstroke        → Ł
        { 0x01A5, '\u013D' },  // XK_Lcaron         → Ľ
        { 0x01A6, '\u015A' },  // XK_Sacute         → Ś
        { 0x01A9, '\u0160' },  // XK_Scaron         → Š
        { 0x01AA, '\u015E' },  // XK_Scedilla       → Ş
        { 0x01AB, '\u0164' },  // XK_Tcaron         → Ť
        { 0x01AC, '\u0179' },  // XK_Zacute         → Ź
        { 0x01AE, '\u017D' },  // XK_Zcaron         → Ž
        { 0x01AF, '\u017B' },  // XK_Zabovedot      → Ż
        { 0x01B1, '\u0105' },  // XK_aogonek        → ą
        { 0x01B2, '\u02DB' },  // XK_ogonek         → ˛
        { 0x01B3, '\u0142' },  // XK_lstroke        → ł
        { 0x01B5, '\u013E' },  // XK_lcaron         → ľ
        { 0x01B6, '\u015B' },  // XK_sacute         → ś
        { 0x01B7, '\u02C7' },  // XK_caron          → ˇ
        { 0x01B9, '\u0161' },  // XK_scaron         → š
        { 0x01BA, '\u015F' },  // XK_scedilla       → ş
        { 0x01BB, '\u0165' },  // XK_tcaron         → ť
        { 0x01BC, '\u017A' },  // XK_zacute         → ź
        { 0x01BD, '\u02DD' },  // XK_doubleacute    → ˝
        { 0x01BE, '\u017E' },  // XK_zcaron         → ž
        { 0x01BF, '\u017C' },  // XK_zabovedot      → ż
        { 0x01C0, '\u0154' },  // XK_Racute         → Ŕ
        { 0x01C3, '\u0102' },  // XK_Abreve         → Ă
        { 0x01C5, '\u0139' },  // XK_Lacute         → Ĺ
        { 0x01C6, '\u0106' },  // XK_Cacute         → Ć
        { 0x01C8, '\u010C' },  // XK_Ccaron         → Č
        { 0x01CA, '\u0118' },  // XK_Eogonek        → Ę
        { 0x01CC, '\u011A' },  // XK_Ecaron         → Ě
        { 0x01CF, '\u010E' },  // XK_Dcaron         → Ď
        { 0x01D0, '\u0110' },  // XK_Dstroke        → Đ
        { 0x01D1, '\u0143' },  // XK_Nacute         → Ń
        { 0x01D2, '\u0147' },  // XK_Ncaron         → Ň
        { 0x01D5, '\u0150' },  // XK_Odoubleacute   → Ő
        { 0x01D8, '\u0158' },  // XK_Rcaron         → Ř
        { 0x01D9, '\u016E' },  // XK_Uring          → Ů
        { 0x01DB, '\u0170' },  // XK_Udoubleacute   → Ű
        { 0x01DE, '\u0162' },  // XK_Tcedilla       → Ţ
        { 0x01E0, '\u0155' },  // XK_racute         → ŕ
        { 0x01E3, '\u0103' },  // XK_abreve         → ă
        { 0x01E5, '\u013A' },  // XK_lacute         → ĺ
        { 0x01E6, '\u0107' },  // XK_cacute         → ć
        { 0x01E8, '\u010D' },  // XK_ccaron         → č
        { 0x01EA, '\u0119' },  // XK_eogonek        → ę
        { 0x01EC, '\u011B' },  // XK_ecaron         → ě
        { 0x01EF, '\u010F' },  // XK_dcaron         → ď
        { 0x01F0, '\u0111' },  // XK_dstroke        → đ
        { 0x01F1, '\u0144' },  // XK_nacute         → ń
        { 0x01F2, '\u0148' },  // XK_ncaron         → ň
        { 0x01F5, '\u0151' },  // XK_odoubleacute   → ő
        { 0x01F8, '\u0159' },  // XK_rcaron         → ř
        { 0x01F9, '\u016F' },  // XK_uring          → ů
        { 0x01FB, '\u0171' },  // XK_udoubleacute   → ű
        { 0x01FE, '\u0163' },  // XK_tcedilla       → ţ
        { 0x01FF, '\u02D9' },  // XK_abovedot       → ˙

        // latin 3 (XK_LATIN3, byte 3 = 2)
        { 0x02A1, '\u0126' },  // XK_Hstroke        → Ħ
        { 0x02A6, '\u0124' },  // XK_Hcircumflex    → Ĥ
        { 0x02A9, '\u0130' },  // XK_Iabovedot      → İ
        { 0x02AB, '\u011E' },  // XK_Gbreve         → Ğ
        { 0x02AC, '\u0134' },  // XK_Jcircumflex    → Ĵ
        { 0x02B1, '\u0127' },  // XK_hstroke        → ħ
        { 0x02B6, '\u0125' },  // XK_hcircumflex    → ĥ
        { 0x02B9, '\u0131' },  // XK_idotless       → ı
        { 0x02BB, '\u011F' },  // XK_gbreve         → ğ
        { 0x02BC, '\u0135' },  // XK_jcircumflex    → ĵ
        { 0x02C5, '\u010A' },  // XK_Cabovedot      → Ċ
        { 0x02C6, '\u0108' },  // XK_Ccircumflex    → Ĉ
        { 0x02D5, '\u0120' },  // XK_Gabovedot      → Ġ
        { 0x02D8, '\u011C' },  // XK_Gcircumflex    → Ĝ
        { 0x02DD, '\u016C' },  // XK_Ubreve         → Ŭ
        { 0x02DE, '\u015C' },  // XK_Scircumflex    → Ŝ
        { 0x02E5, '\u010B' },  // XK_cabovedot      → ċ
        { 0x02E6, '\u0109' },  // XK_ccircumflex    → ĉ
        { 0x02F5, '\u0121' },  // XK_gabovedot      → ġ
        { 0x02F8, '\u011D' },  // XK_gcircumflex    → ĝ
        { 0x02FD, '\u016D' },  // XK_ubreve         → ŭ
        { 0x02FE, '\u015D' },  // XK_scircumflex    → ŝ

        // latin 4 (XK_LATIN4, byte 3 = 3)
        { 0x03A2, '\u0138' },  // XK_kra            → ĸ
        { 0x03A3, '\u0156' },  // XK_Rcedilla       → Ŗ
        { 0x03A5, '\u0128' },  // XK_Itilde         → Ĩ
        { 0x03A6, '\u013B' },  // XK_Lcedilla       → Ļ
        { 0x03AA, '\u0112' },  // XK_Emacron        → Ē
        { 0x03AB, '\u0122' },  // XK_Gcedilla       → Ģ
        { 0x03AC, '\u0166' },  // XK_Tslash         → Ŧ
        { 0x03B3, '\u0157' },  // XK_rcedilla       → ŗ
        { 0x03B5, '\u0129' },  // XK_itilde         → ĩ
        { 0x03B6, '\u013C' },  // XK_lcedilla       → ļ
        { 0x03BA, '\u0113' },  // XK_emacron        → ē
        { 0x03BB, '\u0123' },  // XK_gcedilla       → ģ
        { 0x03BC, '\u0167' },  // XK_tslash         → ŧ
        { 0x03BD, '\u014A' },  // XK_ENG            → Ŋ
        { 0x03BF, '\u014B' },  // XK_eng            → ŋ
        { 0x03C0, '\u0100' },  // XK_Amacron        → Ā
        { 0x03C7, '\u012E' },  // XK_Iogonek        → Į
        { 0x03CC, '\u0116' },  // XK_Eabovedot      → Ė
        { 0x03CF, '\u012A' },  // XK_Imacron        → Ī
        { 0x03D1, '\u0145' },  // XK_Ncedilla       → Ņ
        { 0x03D2, '\u014C' },  // XK_Omacron        → Ō
        { 0x03D3, '\u0136' },  // XK_Kcedilla       → Ķ
        { 0x03D9, '\u0172' },  // XK_Uogonek        → Ų
        { 0x03DD, '\u0168' },  // XK_Utilde         → Ũ
        { 0x03DE, '\u016A' },  // XK_Umacron        → Ū
        { 0x03E0, '\u0101' },  // XK_amacron        → ā
        { 0x03E7, '\u012F' },  // XK_iogonek        → į
        { 0x03EC, '\u0117' },  // XK_eabovedot      → ė
        { 0x03EF, '\u012B' },  // XK_imacron        → ī
        { 0x03F1, '\u0146' },  // XK_ncedilla       → ņ
        { 0x03F2, '\u014D' },  // XK_omacron        → ō
        { 0x03F3, '\u0137' },  // XK_kcedilla       → ķ
        { 0x03F9, '\u0173' },  // XK_uogonek        → ų
        { 0x03FD, '\u0169' },  // XK_utilde         → ũ
        { 0x03FE, '\u016B' },  // XK_umacron        → ū

        // katakana (XK_KATAKANA, byte 3 = 4)
        { 0x047E, '\u203E' },  // XK_overline           → ‾
        { 0x04A1, '\u3002' },  // XK_kana_fullstop      → 。
        { 0x04A2, '\u300C' },  // XK_kana_openingbracket → 「
        { 0x04A3, '\u300D' },  // XK_kana_closingbracket → 」
        { 0x04A4, '\u3001' },  // XK_kana_comma         → 、
        { 0x04A5, '\u30FB' },  // XK_kana_conjunctive   → ・
        { 0x04A6, '\u30F2' },  // XK_kana_WO            → ヲ
        { 0x04A7, '\u30A1' },  // XK_kana_a             → ァ
        { 0x04A8, '\u30A3' },  // XK_kana_i             → ィ
        { 0x04A9, '\u30A5' },  // XK_kana_u             → ゥ
        { 0x04AA, '\u30A7' },  // XK_kana_e             → ェ
        { 0x04AB, '\u30A9' },  // XK_kana_o             → ォ
        { 0x04AC, '\u30E3' },  // XK_kana_ya            → ャ
        { 0x04AD, '\u30E5' },  // XK_kana_yu            → ュ
        { 0x04AE, '\u30E7' },  // XK_kana_yo            → ョ
        { 0x04AF, '\u30C3' },  // XK_kana_tsu           → ッ
        { 0x04B0, '\u30FC' },  // XK_prolongedsound     → ー
        { 0x04B1, '\u30A2' },  // XK_kana_A             → ア
        { 0x04B2, '\u30A4' },  // XK_kana_I             → イ
        { 0x04B3, '\u30A6' },  // XK_kana_U             → ウ
        { 0x04B4, '\u30A8' },  // XK_kana_E             → エ
        { 0x04B5, '\u30AA' },  // XK_kana_O             → オ
        { 0x04B6, '\u30AB' },  // XK_kana_KA            → カ
        { 0x04B7, '\u30AD' },  // XK_kana_KI            → キ
        { 0x04B8, '\u30AF' },  // XK_kana_KU            → ク
        { 0x04B9, '\u30B1' },  // XK_kana_KE            → ケ
        { 0x04BA, '\u30B3' },  // XK_kana_KO            → コ
        { 0x04BB, '\u30B5' },  // XK_kana_SA            → サ
        { 0x04BC, '\u30B7' },  // XK_kana_SHI           → シ
        { 0x04BD, '\u30B9' },  // XK_kana_SU            → ス
        { 0x04BE, '\u30BB' },  // XK_kana_SE            → セ
        { 0x04BF, '\u30BD' },  // XK_kana_SO            → ソ
        { 0x04C0, '\u30BF' },  // XK_kana_TA            → タ
        { 0x04C1, '\u30C1' },  // XK_kana_CHI           → チ
        { 0x04C2, '\u30C4' },  // XK_kana_TSU           → ツ
        { 0x04C3, '\u30C6' },  // XK_kana_TE            → テ
        { 0x04C4, '\u30C8' },  // XK_kana_TO            → ト
        { 0x04C5, '\u30CA' },  // XK_kana_NA            → ナ
        { 0x04C6, '\u30CB' },  // XK_kana_NI            → ニ
        { 0x04C7, '\u30CC' },  // XK_kana_NU            → ヌ
        { 0x04C8, '\u30CD' },  // XK_kana_NE            → ネ
        { 0x04C9, '\u30CE' },  // XK_kana_NO            → ノ
        { 0x04CA, '\u30CF' },  // XK_kana_HA            → ハ
        { 0x04CB, '\u30D2' },  // XK_kana_HI            → ヒ
        { 0x04CC, '\u30D5' },  // XK_kana_FU            → フ
        { 0x04CD, '\u30D8' },  // XK_kana_HE            → ヘ
        { 0x04CE, '\u30DB' },  // XK_kana_HO            → ホ
        { 0x04CF, '\u30DE' },  // XK_kana_MA            → マ
        { 0x04D0, '\u30DF' },  // XK_kana_MI            → ミ
        { 0x04D1, '\u30E0' },  // XK_kana_MU            → ム
        { 0x04D2, '\u30E1' },  // XK_kana_ME            → メ
        { 0x04D3, '\u30E2' },  // XK_kana_MO            → モ
        { 0x04D4, '\u30E4' },  // XK_kana_YA            → ヤ
        { 0x04D5, '\u30E6' },  // XK_kana_YU            → ユ
        { 0x04D6, '\u30E8' },  // XK_kana_YO            → ヨ
        { 0x04D7, '\u30E9' },  // XK_kana_RA            → ラ
        { 0x04D8, '\u30EA' },  // XK_kana_RI            → リ
        { 0x04D9, '\u30EB' },  // XK_kana_RU            → ル
        { 0x04DA, '\u30EC' },  // XK_kana_RE            → レ
        { 0x04DB, '\u30ED' },  // XK_kana_RO            → ロ
        { 0x04DC, '\u30EF' },  // XK_kana_WA            → ワ
        { 0x04DD, '\u30F3' },  // XK_kana_N             → ン
        { 0x04DE, '\u309B' },  // XK_voicedsound        → ゛
        { 0x04DF, '\u309C' },  // XK_semivoicedsound    → ゜

        // arabic (XK_ARABIC, byte 3 = 5)
        { 0x05AC, '\u060C' },  // XK_Arabic_comma
        { 0x05BB, '\u061B' },  // XK_Arabic_semicolon
        { 0x05BF, '\u061F' },  // XK_Arabic_question_mark
        { 0x05C1, '\u0621' },  // XK_Arabic_hamza
        { 0x05C2, '\u0622' },  // XK_Arabic_maddaonalef
        { 0x05C3, '\u0623' },  // XK_Arabic_hamzaonalef
        { 0x05C4, '\u0624' },  // XK_Arabic_hamzaonwaw
        { 0x05C5, '\u0625' },  // XK_Arabic_hamzaunderalef
        { 0x05C6, '\u0626' },  // XK_Arabic_hamzaonyeh
        { 0x05C7, '\u0627' },  // XK_Arabic_alef
        { 0x05C8, '\u0628' },  // XK_Arabic_beh
        { 0x05C9, '\u0629' },  // XK_Arabic_tehmarbuta
        { 0x05CA, '\u062A' },  // XK_Arabic_teh
        { 0x05CB, '\u062B' },  // XK_Arabic_theh
        { 0x05CC, '\u062C' },  // XK_Arabic_jeem
        { 0x05CD, '\u062D' },  // XK_Arabic_hah
        { 0x05CE, '\u062E' },  // XK_Arabic_khah
        { 0x05CF, '\u062F' },  // XK_Arabic_dal
        { 0x05D0, '\u0630' },  // XK_Arabic_thal
        { 0x05D1, '\u0631' },  // XK_Arabic_ra
        { 0x05D2, '\u0632' },  // XK_Arabic_zain
        { 0x05D3, '\u0633' },  // XK_Arabic_seen
        { 0x05D4, '\u0634' },  // XK_Arabic_sheen
        { 0x05D5, '\u0635' },  // XK_Arabic_sad
        { 0x05D6, '\u0636' },  // XK_Arabic_dad
        { 0x05D7, '\u0637' },  // XK_Arabic_tah
        { 0x05D8, '\u0638' },  // XK_Arabic_zah
        { 0x05D9, '\u0639' },  // XK_Arabic_ain
        { 0x05DA, '\u063A' },  // XK_Arabic_ghain
        { 0x05E0, '\u0640' },  // XK_Arabic_tatweel
        { 0x05E1, '\u0641' },  // XK_Arabic_feh
        { 0x05E2, '\u0642' },  // XK_Arabic_qaf
        { 0x05E3, '\u0643' },  // XK_Arabic_kaf
        { 0x05E4, '\u0644' },  // XK_Arabic_lam
        { 0x05E5, '\u0645' },  // XK_Arabic_meem
        { 0x05E6, '\u0646' },  // XK_Arabic_noon
        { 0x05E7, '\u0647' },  // XK_Arabic_ha
        { 0x05E8, '\u0648' },  // XK_Arabic_waw
        { 0x05E9, '\u0649' },  // XK_Arabic_alefmaksura
        { 0x05EA, '\u064A' },  // XK_Arabic_yeh
        { 0x05EB, '\u064B' },  // XK_Arabic_fathatan
        { 0x05EC, '\u064C' },  // XK_Arabic_dammatan
        { 0x05ED, '\u064D' },  // XK_Arabic_kasratan
        { 0x05EE, '\u064E' },  // XK_Arabic_fatha
        { 0x05EF, '\u064F' },  // XK_Arabic_damma
        { 0x05F0, '\u0650' },  // XK_Arabic_kasra
        { 0x05F1, '\u0651' },  // XK_Arabic_shadda
        { 0x05F2, '\u0652' },  // XK_Arabic_sukun

        // cyrillic (XK_CYRILLIC, byte 3 = 6)
        { 0x06A1, '\u0452' },  // XK_Serbian_dje
        { 0x06A2, '\u0453' },  // XK_Macedonia_gje
        { 0x06A3, '\u0451' },  // XK_Cyrillic_io
        { 0x06A4, '\u0454' },  // XK_Ukrainian_ie
        { 0x06A5, '\u0455' },  // XK_Macedonia_dse
        { 0x06A6, '\u0456' },  // XK_Ukrainian_i
        { 0x06A7, '\u0457' },  // XK_Ukrainian_yi
        { 0x06A8, '\u0458' },  // XK_Cyrillic_je
        { 0x06A9, '\u0459' },  // XK_Cyrillic_lje
        { 0x06AA, '\u045A' },  // XK_Cyrillic_nje
        { 0x06AB, '\u045B' },  // XK_Serbian_tshe
        { 0x06AC, '\u045C' },  // XK_Macedonia_kje
        { 0x06AE, '\u045E' },  // XK_Byelorussian_shortu
        { 0x06AF, '\u045F' },  // XK_Cyrillic_dzhe
        { 0x06B0, '\u2116' },  // XK_numerosign
        { 0x06B1, '\u0402' },  // XK_Serbian_DJE
        { 0x06B2, '\u0403' },  // XK_Macedonia_GJE
        { 0x06B3, '\u0401' },  // XK_Cyrillic_IO
        { 0x06B4, '\u0404' },  // XK_Ukrainian_IE
        { 0x06B5, '\u0405' },  // XK_Macedonia_DSE
        { 0x06B6, '\u0406' },  // XK_Ukrainian_I
        { 0x06B7, '\u0407' },  // XK_Ukrainian_YI
        { 0x06B8, '\u0408' },  // XK_Cyrillic_JE
        { 0x06B9, '\u0409' },  // XK_Cyrillic_LJE
        { 0x06BA, '\u040A' },  // XK_Cyrillic_NJE
        { 0x06BB, '\u040B' },  // XK_Serbian_TSHE
        { 0x06BC, '\u040C' },  // XK_Macedonia_KJE
        { 0x06BE, '\u040E' },  // XK_Byelorussian_SHORTU
        { 0x06BF, '\u040F' },  // XK_Cyrillic_DZHE
        { 0x06C0, '\u044E' },  // XK_Cyrillic_yu
        { 0x06C1, '\u0430' },  // XK_Cyrillic_a
        { 0x06C2, '\u0431' },  // XK_Cyrillic_be
        { 0x06C3, '\u0446' },  // XK_Cyrillic_tse
        { 0x06C4, '\u0434' },  // XK_Cyrillic_de
        { 0x06C5, '\u0435' },  // XK_Cyrillic_ie
        { 0x06C6, '\u0444' },  // XK_Cyrillic_ef
        { 0x06C7, '\u0433' },  // XK_Cyrillic_ghe
        { 0x06C8, '\u0445' },  // XK_Cyrillic_ha
        { 0x06C9, '\u0438' },  // XK_Cyrillic_i
        { 0x06CA, '\u0439' },  // XK_Cyrillic_shorti
        { 0x06CB, '\u043A' },  // XK_Cyrillic_ka
        { 0x06CC, '\u043B' },  // XK_Cyrillic_el
        { 0x06CD, '\u043C' },  // XK_Cyrillic_em
        { 0x06CE, '\u043D' },  // XK_Cyrillic_en
        { 0x06CF, '\u043E' },  // XK_Cyrillic_o
        { 0x06D0, '\u043F' },  // XK_Cyrillic_pe
        { 0x06D1, '\u044F' },  // XK_Cyrillic_ya
        { 0x06D2, '\u0440' },  // XK_Cyrillic_er
        { 0x06D3, '\u0441' },  // XK_Cyrillic_es
        { 0x06D4, '\u0442' },  // XK_Cyrillic_te
        { 0x06D5, '\u0443' },  // XK_Cyrillic_u
        { 0x06D6, '\u0436' },  // XK_Cyrillic_zhe
        { 0x06D7, '\u0432' },  // XK_Cyrillic_ve
        { 0x06D8, '\u044C' },  // XK_Cyrillic_softsign
        { 0x06D9, '\u044B' },  // XK_Cyrillic_yeru
        { 0x06DA, '\u0437' },  // XK_Cyrillic_ze
        { 0x06DB, '\u0448' },  // XK_Cyrillic_sha
        { 0x06DC, '\u044D' },  // XK_Cyrillic_e
        { 0x06DD, '\u0449' },  // XK_Cyrillic_shcha
        { 0x06DE, '\u0447' },  // XK_Cyrillic_che
        { 0x06DF, '\u044A' },  // XK_Cyrillic_hardsign
        { 0x06E0, '\u042E' },  // XK_Cyrillic_YU
        { 0x06E1, '\u0410' },  // XK_Cyrillic_A
        { 0x06E2, '\u0411' },  // XK_Cyrillic_BE
        { 0x06E3, '\u0426' },  // XK_Cyrillic_TSE
        { 0x06E4, '\u0414' },  // XK_Cyrillic_DE
        { 0x06E5, '\u0415' },  // XK_Cyrillic_IE
        { 0x06E6, '\u0424' },  // XK_Cyrillic_EF
        { 0x06E7, '\u0413' },  // XK_Cyrillic_GHE
        { 0x06E8, '\u0425' },  // XK_Cyrillic_HA
        { 0x06E9, '\u0418' },  // XK_Cyrillic_I
        { 0x06EA, '\u0419' },  // XK_Cyrillic_SHORTI
        { 0x06EB, '\u041A' },  // XK_Cyrillic_KA
        { 0x06EC, '\u041B' },  // XK_Cyrillic_EL
        { 0x06ED, '\u041C' },  // XK_Cyrillic_EM
        { 0x06EE, '\u041D' },  // XK_Cyrillic_EN
        { 0x06EF, '\u041E' },  // XK_Cyrillic_O
        { 0x06F0, '\u041F' },  // XK_Cyrillic_PE
        { 0x06F1, '\u042F' },  // XK_Cyrillic_YA
        { 0x06F2, '\u0420' },  // XK_Cyrillic_ER
        { 0x06F3, '\u0421' },  // XK_Cyrillic_ES
        { 0x06F4, '\u0422' },  // XK_Cyrillic_TE
        { 0x06F5, '\u0423' },  // XK_Cyrillic_U
        { 0x06F6, '\u0416' },  // XK_Cyrillic_ZHE
        { 0x06F7, '\u0412' },  // XK_Cyrillic_VE
        { 0x06F8, '\u042C' },  // XK_Cyrillic_SOFTSIGN
        { 0x06F9, '\u042B' },  // XK_Cyrillic_YERU
        { 0x06FA, '\u0417' },  // XK_Cyrillic_ZE
        { 0x06FB, '\u0428' },  // XK_Cyrillic_SHA
        { 0x06FC, '\u042D' },  // XK_Cyrillic_E
        { 0x06FD, '\u0429' },  // XK_Cyrillic_SHCHA
        { 0x06FE, '\u0427' },  // XK_Cyrillic_CHE
        { 0x06FF, '\u042A' },  // XK_Cyrillic_HARDSIGN

        // greek (XK_GREEK, byte 3 = 7)
        { 0x07A1, '\u0386' },  // XK_Greek_ALPHAaccent
        { 0x07A2, '\u0388' },  // XK_Greek_EPSILONaccent
        { 0x07A3, '\u0389' },  // XK_Greek_ETAaccent
        { 0x07A4, '\u038A' },  // XK_Greek_IOTAaccent
        { 0x07A5, '\u03AA' },  // XK_Greek_IOTAdiaeresis
        { 0x07A7, '\u038C' },  // XK_Greek_OMICRONaccent
        { 0x07A8, '\u038E' },  // XK_Greek_UPSILONaccent
        { 0x07A9, '\u03AB' },  // XK_Greek_UPSILONdieresis
        { 0x07AB, '\u038F' },  // XK_Greek_OMEGAaccent
        { 0x07AE, '\u0385' },  // XK_Greek_accentdieresis
        { 0x07AF, '\u2015' },  // XK_Greek_horizbar
        { 0x07B1, '\u03AC' },  // XK_Greek_alphaaccent
        { 0x07B2, '\u03AD' },  // XK_Greek_epsilonaccent
        { 0x07B3, '\u03AE' },  // XK_Greek_etaaccent
        { 0x07B4, '\u03AF' },  // XK_Greek_iotaaccent
        { 0x07B5, '\u03CA' },  // XK_Greek_iotadieresis
        { 0x07B6, '\u0390' },  // XK_Greek_iotaaccentdieresis
        { 0x07B7, '\u03CC' },  // XK_Greek_omicronaccent
        { 0x07B8, '\u03CD' },  // XK_Greek_upsilonaccent
        { 0x07B9, '\u03CB' },  // XK_Greek_upsilondieresis
        { 0x07BA, '\u03B0' },  // XK_Greek_upsilonaccentdieresis
        { 0x07BB, '\u03CE' },  // XK_Greek_omegaaccent
        { 0x07C1, '\u0391' },  // XK_Greek_ALPHA
        { 0x07C2, '\u0392' },  // XK_Greek_BETA
        { 0x07C3, '\u0393' },  // XK_Greek_GAMMA
        { 0x07C4, '\u0394' },  // XK_Greek_DELTA
        { 0x07C5, '\u0395' },  // XK_Greek_EPSILON
        { 0x07C6, '\u0396' },  // XK_Greek_ZETA
        { 0x07C7, '\u0397' },  // XK_Greek_ETA
        { 0x07C8, '\u0398' },  // XK_Greek_THETA
        { 0x07C9, '\u0399' },  // XK_Greek_IOTA
        { 0x07CA, '\u039A' },  // XK_Greek_KAPPA
        { 0x07CB, '\u039B' },  // XK_Greek_LAMBDA
        { 0x07CC, '\u039C' },  // XK_Greek_MU
        { 0x07CD, '\u039D' },  // XK_Greek_NU
        { 0x07CE, '\u039E' },  // XK_Greek_XI
        { 0x07CF, '\u039F' },  // XK_Greek_OMICRON
        { 0x07D0, '\u03A0' },  // XK_Greek_PI
        { 0x07D1, '\u03A1' },  // XK_Greek_RHO
        { 0x07D2, '\u03A3' },  // XK_Greek_SIGMA
        { 0x07D4, '\u03A4' },  // XK_Greek_TAU
        { 0x07D5, '\u03A5' },  // XK_Greek_UPSILON
        { 0x07D6, '\u03A6' },  // XK_Greek_PHI
        { 0x07D7, '\u03A7' },  // XK_Greek_CHI
        { 0x07D8, '\u03A8' },  // XK_Greek_PSI
        { 0x07D9, '\u03A9' },  // XK_Greek_OMEGA
        { 0x07E1, '\u03B1' },  // XK_Greek_alpha
        { 0x07E2, '\u03B2' },  // XK_Greek_beta
        { 0x07E3, '\u03B3' },  // XK_Greek_gamma
        { 0x07E4, '\u03B4' },  // XK_Greek_delta
        { 0x07E5, '\u03B5' },  // XK_Greek_epsilon
        { 0x07E6, '\u03B6' },  // XK_Greek_zeta
        { 0x07E7, '\u03B7' },  // XK_Greek_eta
        { 0x07E8, '\u03B8' },  // XK_Greek_theta
        { 0x07E9, '\u03B9' },  // XK_Greek_iota
        { 0x07EA, '\u03BA' },  // XK_Greek_kappa
        { 0x07EB, '\u03BB' },  // XK_Greek_lambda
        { 0x07EC, '\u03BC' },  // XK_Greek_mu
        { 0x07ED, '\u03BD' },  // XK_Greek_nu
        { 0x07EE, '\u03BE' },  // XK_Greek_xi
        { 0x07EF, '\u03BF' },  // XK_Greek_omicron
        { 0x07F0, '\u03C0' },  // XK_Greek_pi
        { 0x07F1, '\u03C1' },  // XK_Greek_rho
        { 0x07F2, '\u03C3' },  // XK_Greek_sigma
        { 0x07F3, '\u03C2' },  // XK_Greek_finalsmallsigma
        { 0x07F4, '\u03C4' },  // XK_Greek_tau
        { 0x07F5, '\u03C5' },  // XK_Greek_upsilon
        { 0x07F6, '\u03C6' },  // XK_Greek_phi
        { 0x07F7, '\u03C7' },  // XK_Greek_chi
        { 0x07F8, '\u03C8' },  // XK_Greek_psi
        { 0x07F9, '\u03C9' },  // XK_Greek_omega

        // technical (XK_TECHNICAL, byte 3 = 8)
        { 0x08A1, '\u23B7' },  // XK_leftradical
        { 0x08A2, '\u250C' },  // XK_topleftradical
        { 0x08A3, '\u2500' },  // XK_horizconnector
        { 0x08A4, '\u2320' },  // XK_topintegral
        { 0x08A5, '\u2321' },  // XK_botintegral
        { 0x08A6, '\u2502' },  // XK_vertconnector
        { 0x08A7, '\u23A1' },  // XK_topleftsqbracket
        { 0x08A8, '\u23A3' },  // XK_botleftsqbracket
        { 0x08A9, '\u23A4' },  // XK_toprightsqbracket
        { 0x08AA, '\u23A6' },  // XK_botrightsqbracket
        { 0x08AB, '\u239B' },  // XK_topleftparens
        { 0x08AC, '\u239D' },  // XK_botleftparens
        { 0x08AD, '\u239E' },  // XK_toprightparens
        { 0x08AE, '\u23A0' },  // XK_botrightparens
        { 0x08AF, '\u23A8' },  // XK_leftmiddlecurlybrace
        { 0x08B0, '\u23AC' },  // XK_rightmiddlecurlybrace
        { 0x08BC, '\u2264' },  // XK_lessthanequal
        { 0x08BD, '\u2260' },  // XK_notequal
        { 0x08BE, '\u2265' },  // XK_greaterthanequal
        { 0x08BF, '\u222B' },  // XK_integral
        { 0x08C0, '\u2234' },  // XK_therefore
        { 0x08C1, '\u221D' },  // XK_variation
        { 0x08C2, '\u221E' },  // XK_infinity
        { 0x08C5, '\u2207' },  // XK_nabla
        { 0x08C8, '\u223C' },  // XK_approximate
        { 0x08C9, '\u2243' },  // XK_similarequal
        { 0x08CD, '\u21D4' },  // XK_ifonlyif
        { 0x08CE, '\u21D2' },  // XK_implies
        { 0x08CF, '\u2261' },  // XK_identical
        { 0x08D6, '\u221A' },  // XK_radical
        { 0x08DA, '\u2282' },  // XK_includedin
        { 0x08DB, '\u2283' },  // XK_includes
        { 0x08DC, '\u2229' },  // XK_intersection
        { 0x08DD, '\u222A' },  // XK_union
        { 0x08DE, '\u2227' },  // XK_logicaland
        { 0x08DF, '\u2228' },  // XK_logicalor
        { 0x08EF, '\u2202' },  // XK_partialderivative
        { 0x08F6, '\u0192' },  // XK_function
        { 0x08FB, '\u2190' },  // XK_leftarrow
        { 0x08FC, '\u2191' },  // XK_uparrow
        { 0x08FD, '\u2192' },  // XK_rightarrow
        { 0x08FE, '\u2193' },  // XK_downarrow

        // special (XK_SPECIAL, byte 3 = 9)
        { 0x09E0, '\u25C6' },  // XK_soliddiamond
        { 0x09E1, '\u2592' },  // XK_checkerboard
        { 0x09E2, '\u2409' },  // XK_ht
        { 0x09E3, '\u240C' },  // XK_ff
        { 0x09E4, '\u240D' },  // XK_cr
        { 0x09E5, '\u240A' },  // XK_lf
        { 0x09E8, '\u2424' },  // XK_nl
        { 0x09E9, '\u240B' },  // XK_vt
        { 0x09EA, '\u2518' },  // XK_lowrightcorner
        { 0x09EB, '\u2510' },  // XK_uprightcorner
        { 0x09EC, '\u250C' },  // XK_upleftcorner
        { 0x09ED, '\u2514' },  // XK_lowleftcorner
        { 0x09EE, '\u253C' },  // XK_crossinglines
        { 0x09EF, '\u23BA' },  // XK_horizlinescan1
        { 0x09F0, '\u23BB' },  // XK_horizlinescan3
        { 0x09F1, '\u2500' },  // XK_horizlinescan5
        { 0x09F2, '\u23BC' },  // XK_horizlinescan7
        { 0x09F3, '\u23BD' },  // XK_horizlinescan9
        { 0x09F4, '\u251C' },  // XK_leftt
        { 0x09F5, '\u2524' },  // XK_rightt
        { 0x09F6, '\u2534' },  // XK_bott
        { 0x09F7, '\u252C' },  // XK_topt
        { 0x09F8, '\u2502' },  // XK_vertbar

        // publishing (XK_PUBLISHING, byte 3 = A)
        { 0x0AA1, '\u2003' },  // XK_emspace
        { 0x0AA2, '\u2002' },  // XK_enspace
        { 0x0AA3, '\u2004' },  // XK_em3space
        { 0x0AA4, '\u2005' },  // XK_em4space
        { 0x0AA5, '\u2007' },  // XK_digitspace
        { 0x0AA6, '\u2008' },  // XK_punctspace
        { 0x0AA7, '\u2009' },  // XK_thinspace
        { 0x0AA8, '\u200A' },  // XK_hairspace
        { 0x0AA9, '\u2014' },  // XK_emdash
        { 0x0AAA, '\u2013' },  // XK_endash
        { 0x0AAE, '\u2026' },  // XK_ellipsis
        { 0x0AAF, '\u2025' },  // XK_doubbaselinedot
        { 0x0AB0, '\u2153' },  // XK_onethird
        { 0x0AB1, '\u2154' },  // XK_twothirds
        { 0x0AB2, '\u2155' },  // XK_onefifth
        { 0x0AB3, '\u2156' },  // XK_twofifths
        { 0x0AB4, '\u2157' },  // XK_threefifths
        { 0x0AB5, '\u2158' },  // XK_fourfifths
        { 0x0AB6, '\u2159' },  // XK_onesixth
        { 0x0AB7, '\u215A' },  // XK_fivesixths
        { 0x0AB8, '\u2105' },  // XK_careof
        { 0x0ABB, '\u2012' },  // XK_figdash
        { 0x0ABC, '\u2329' },  // XK_leftanglebracket
        { 0x0ABE, '\u232A' },  // XK_rightanglebracket
        { 0x0AC3, '\u215B' },  // XK_oneeighth
        { 0x0AC4, '\u215C' },  // XK_threeeighths
        { 0x0AC5, '\u215D' },  // XK_fiveeighths
        { 0x0AC6, '\u215E' },  // XK_seveneighths
        { 0x0AC9, '\u2122' },  // XK_trademark
        { 0x0ACA, '\u2613' },  // XK_signaturemark
        { 0x0ACC, '\u25C1' },  // XK_leftopentriangle
        { 0x0ACD, '\u25B7' },  // XK_rightopentriangle
        { 0x0ACE, '\u25CB' },  // XK_emopencircle
        { 0x0ACF, '\u25AF' },  // XK_emopenrectangle
        { 0x0AD0, '\u2018' },  // XK_leftsinglequotemark
        { 0x0AD1, '\u2019' },  // XK_rightsinglequotemark
        { 0x0AD2, '\u201C' },  // XK_leftdoublequotemark
        { 0x0AD3, '\u201D' },  // XK_rightdoublequotemark
        { 0x0AD4, '\u211E' },  // XK_prescription
        { 0x0AD6, '\u2032' },  // XK_minutes
        { 0x0AD7, '\u2033' },  // XK_seconds
        { 0x0AD9, '\u271D' },  // XK_latincross
        { 0x0ADB, '\u25AC' },  // XK_filledrectbullet
        { 0x0ADC, '\u25C0' },  // XK_filledlefttribullet
        { 0x0ADD, '\u25B6' },  // XK_filledrighttribullet
        { 0x0ADE, '\u25CF' },  // XK_emfilledcircle
        { 0x0ADF, '\u25AE' },  // XK_emfilledrect
        { 0x0AE0, '\u25E6' },  // XK_enopencircbullet
        { 0x0AE1, '\u25AB' },  // XK_enopensquarebullet
        { 0x0AE2, '\u25AD' },  // XK_openrectbullet
        { 0x0AE3, '\u25B3' },  // XK_opentribulletup
        { 0x0AE4, '\u25BD' },  // XK_opentribulletdown
        { 0x0AE5, '\u2606' },  // XK_openstar
        { 0x0AE6, '\u2022' },  // XK_enfilledcircbullet
        { 0x0AE7, '\u25AA' },  // XK_enfilledsqbullet
        { 0x0AE8, '\u25B2' },  // XK_filledtribulletup
        { 0x0AE9, '\u25BC' },  // XK_filledtribulletdown
        { 0x0AEA, '\u261C' },  // XK_leftpointer
        { 0x0AEB, '\u261E' },  // XK_rightpointer
        { 0x0AEC, '\u2663' },  // XK_club
        { 0x0AED, '\u2666' },  // XK_diamond
        { 0x0AEE, '\u2665' },  // XK_heart
        { 0x0AF0, '\u2720' },  // XK_maltesecross
        { 0x0AF1, '\u2020' },  // XK_dagger
        { 0x0AF2, '\u2021' },  // XK_doubledagger
        { 0x0AF3, '\u2713' },  // XK_checkmark
        { 0x0AF4, '\u2717' },  // XK_ballotcross
        { 0x0AF5, '\u266F' },  // XK_musicalsharp
        { 0x0AF6, '\u266D' },  // XK_musicalflat
        { 0x0AF7, '\u2642' },  // XK_malesymbol
        { 0x0AF8, '\u2640' },  // XK_femalesymbol
        { 0x0AF9, '\u260E' },  // XK_telephone
        { 0x0AFA, '\u2315' },  // XK_telephonerecorder
        { 0x0AFB, '\u2117' },  // XK_phonographcopyright
        { 0x0AFC, '\u2038' },  // XK_caret
        { 0x0AFD, '\u201A' },  // XK_singlelowquotemark
        { 0x0AFE, '\u201E' },  // XK_doublelowquotemark

        // APL (XK_APL, byte 3 = B)
        { 0x0BA3, '\u003C' },  // XK_leftcaret
        { 0x0BA6, '\u003E' },  // XK_rightcaret
        { 0x0BA8, '\u2228' },  // XK_downcaret
        { 0x0BA9, '\u2227' },  // XK_upcaret
        { 0x0BC0, '\u00AF' },  // XK_overbar
        { 0x0BC2, '\u22A5' },  // XK_downtack
        { 0x0BC3, '\u2229' },  // XK_upshoe
        { 0x0BC4, '\u230A' },  // XK_downstile
        { 0x0BC6, '\u005F' },  // XK_underbar
        { 0x0BCA, '\u2218' },  // XK_jot
        { 0x0BCC, '\u2395' },  // XK_quad
        { 0x0BCE, '\u22A4' },  // XK_uptack
        { 0x0BCF, '\u25CB' },  // XK_circle
        { 0x0BD3, '\u2308' },  // XK_upstile
        { 0x0BD6, '\u222A' },  // XK_downshoe
        { 0x0BD8, '\u2283' },  // XK_rightshoe
        { 0x0BDA, '\u2282' },  // XK_leftshoe
        { 0x0BDC, '\u22A2' },  // XK_lefttack
        { 0x0BFC, '\u22A3' },  // XK_righttack

        // hebrew (XK_HEBREW, byte 3 = C)
        { 0x0CDF, '\u2017' },  // XK_hebrew_doublelowline
        { 0x0CE0, '\u05D0' },  // XK_hebrew_aleph
        { 0x0CE1, '\u05D1' },  // XK_hebrew_bet
        { 0x0CE2, '\u05D2' },  // XK_hebrew_gimel
        { 0x0CE3, '\u05D3' },  // XK_hebrew_dalet
        { 0x0CE4, '\u05D4' },  // XK_hebrew_he
        { 0x0CE5, '\u05D5' },  // XK_hebrew_waw
        { 0x0CE6, '\u05D6' },  // XK_hebrew_zain
        { 0x0CE7, '\u05D7' },  // XK_hebrew_chet
        { 0x0CE8, '\u05D8' },  // XK_hebrew_tet
        { 0x0CE9, '\u05D9' },  // XK_hebrew_yod
        { 0x0CEA, '\u05DA' },  // XK_hebrew_finalkaph
        { 0x0CEB, '\u05DB' },  // XK_hebrew_kaph
        { 0x0CEC, '\u05DC' },  // XK_hebrew_lamed
        { 0x0CED, '\u05DD' },  // XK_hebrew_finalmem
        { 0x0CEE, '\u05DE' },  // XK_hebrew_mem
        { 0x0CEF, '\u05DF' },  // XK_hebrew_finalnun
        { 0x0CF0, '\u05E0' },  // XK_hebrew_nun
        { 0x0CF1, '\u05E1' },  // XK_hebrew_samech
        { 0x0CF2, '\u05E2' },  // XK_hebrew_ayin
        { 0x0CF3, '\u05E3' },  // XK_hebrew_finalpe
        { 0x0CF4, '\u05E4' },  // XK_hebrew_pe
        { 0x0CF5, '\u05E5' },  // XK_hebrew_finalzade
        { 0x0CF6, '\u05E6' },  // XK_hebrew_zade
        { 0x0CF7, '\u05E7' },  // XK_hebrew_qoph
        { 0x0CF8, '\u05E8' },  // XK_hebrew_resh
        { 0x0CF9, '\u05E9' },  // XK_hebrew_shin
        { 0x0CFA, '\u05EA' },  // XK_hebrew_taw

        // thai (XK_THAI, byte 3 = D)
        { 0x0DA1, '\u0E01' },  // XK_Thai_kokai
        { 0x0DA2, '\u0E02' },  // XK_Thai_khokhai
        { 0x0DA3, '\u0E03' },  // XK_Thai_khokhuat
        { 0x0DA4, '\u0E04' },  // XK_Thai_khokhwai
        { 0x0DA5, '\u0E05' },  // XK_Thai_khokhon
        { 0x0DA6, '\u0E06' },  // XK_Thai_khorakhang
        { 0x0DA7, '\u0E07' },  // XK_Thai_ngongu
        { 0x0DA8, '\u0E08' },  // XK_Thai_chochan
        { 0x0DA9, '\u0E09' },  // XK_Thai_choching
        { 0x0DAA, '\u0E0A' },  // XK_Thai_chochang
        { 0x0DAB, '\u0E0B' },  // XK_Thai_soso
        { 0x0DAC, '\u0E0C' },  // XK_Thai_chochoe
        { 0x0DAD, '\u0E0D' },  // XK_Thai_yoying
        { 0x0DAE, '\u0E0E' },  // XK_Thai_dochada
        { 0x0DAF, '\u0E0F' },  // XK_Thai_topatak
        { 0x0DB0, '\u0E10' },  // XK_Thai_thothan
        { 0x0DB1, '\u0E11' },  // XK_Thai_thonangmontho
        { 0x0DB2, '\u0E12' },  // XK_Thai_thophuthao
        { 0x0DB3, '\u0E13' },  // XK_Thai_nonen
        { 0x0DB4, '\u0E14' },  // XK_Thai_dodek
        { 0x0DB5, '\u0E15' },  // XK_Thai_totao
        { 0x0DB6, '\u0E16' },  // XK_Thai_thothung
        { 0x0DB7, '\u0E17' },  // XK_Thai_thothahan
        { 0x0DB8, '\u0E18' },  // XK_Thai_thothong
        { 0x0DB9, '\u0E19' },  // XK_Thai_nonu
        { 0x0DBA, '\u0E1A' },  // XK_Thai_bobaimai
        { 0x0DBB, '\u0E1B' },  // XK_Thai_popla
        { 0x0DBC, '\u0E1C' },  // XK_Thai_phophung
        { 0x0DBD, '\u0E1D' },  // XK_Thai_fofa
        { 0x0DBE, '\u0E1E' },  // XK_Thai_phophan
        { 0x0DBF, '\u0E1F' },  // XK_Thai_fofan
        { 0x0DC0, '\u0E20' },  // XK_Thai_phosamphao
        { 0x0DC1, '\u0E21' },  // XK_Thai_moma
        { 0x0DC2, '\u0E22' },  // XK_Thai_yoyak
        { 0x0DC3, '\u0E23' },  // XK_Thai_rorua
        { 0x0DC4, '\u0E24' },  // XK_Thai_ru
        { 0x0DC5, '\u0E25' },  // XK_Thai_loling
        { 0x0DC6, '\u0E26' },  // XK_Thai_lu
        { 0x0DC7, '\u0E27' },  // XK_Thai_wowaen
        { 0x0DC8, '\u0E28' },  // XK_Thai_sosala
        { 0x0DC9, '\u0E29' },  // XK_Thai_sorusi
        { 0x0DCA, '\u0E2A' },  // XK_Thai_sosua
        { 0x0DCB, '\u0E2B' },  // XK_Thai_hohip
        { 0x0DCC, '\u0E2C' },  // XK_Thai_lochula
        { 0x0DCD, '\u0E2D' },  // XK_Thai_oang
        { 0x0DCE, '\u0E2E' },  // XK_Thai_honokhuk
        { 0x0DCF, '\u0E2F' },  // XK_Thai_paiyannoi
        { 0x0DD0, '\u0E30' },  // XK_Thai_saraa
        { 0x0DD1, '\u0E31' },  // XK_Thai_maihanakat
        { 0x0DD2, '\u0E32' },  // XK_Thai_saraaa
        { 0x0DD3, '\u0E33' },  // XK_Thai_saraam
        { 0x0DD4, '\u0E34' },  // XK_Thai_sarai
        { 0x0DD5, '\u0E35' },  // XK_Thai_saraii
        { 0x0DD6, '\u0E36' },  // XK_Thai_saraue
        { 0x0DD7, '\u0E37' },  // XK_Thai_sarauee
        { 0x0DD8, '\u0E38' },  // XK_Thai_sarau
        { 0x0DD9, '\u0E39' },  // XK_Thai_sarauu
        { 0x0DDA, '\u0E3A' },  // XK_Thai_phinthu
        { 0x0DDF, '\u0E3F' },  // XK_Thai_baht
        { 0x0DE0, '\u0E40' },  // XK_Thai_sarae
        { 0x0DE1, '\u0E41' },  // XK_Thai_saraae
        { 0x0DE2, '\u0E42' },  // XK_Thai_sarao
        { 0x0DE3, '\u0E43' },  // XK_Thai_saraaimaimuan
        { 0x0DE4, '\u0E44' },  // XK_Thai_saraaimaimalai
        { 0x0DE5, '\u0E45' },  // XK_Thai_lakkhangyao
        { 0x0DE6, '\u0E46' },  // XK_Thai_maiyamok
        { 0x0DE7, '\u0E47' },  // XK_Thai_maitaikhu
        { 0x0DE8, '\u0E48' },  // XK_Thai_maiek
        { 0x0DE9, '\u0E49' },  // XK_Thai_maitho
        { 0x0DEA, '\u0E4A' },  // XK_Thai_maitri
        { 0x0DEB, '\u0E4B' },  // XK_Thai_maichattawa
        { 0x0DEC, '\u0E4C' },  // XK_Thai_thanthakhat
        { 0x0DED, '\u0E4D' },  // XK_Thai_nikhahit
        { 0x0DF0, '\u0E50' },  // XK_Thai_leksun
        { 0x0DF1, '\u0E51' },  // XK_Thai_leknung
        { 0x0DF2, '\u0E52' },  // XK_Thai_leksong
        { 0x0DF3, '\u0E53' },  // XK_Thai_leksam
        { 0x0DF4, '\u0E54' },  // XK_Thai_leksi
        { 0x0DF5, '\u0E55' },  // XK_Thai_lekha
        { 0x0DF6, '\u0E56' },  // XK_Thai_lekhok
        { 0x0DF7, '\u0E57' },  // XK_Thai_lekchet
        { 0x0DF8, '\u0E58' },  // XK_Thai_lekpaet
        { 0x0DF9, '\u0E59' },  // XK_Thai_lekkao

        // hangul (XK_KOREAN, byte 3 = E)
        { 0x0EA1, '\u3131' },  // XK_Hangul_Kiyeog
        { 0x0EA2, '\u3132' },  // XK_Hangul_SsangKiyeog
        { 0x0EA3, '\u3133' },  // XK_Hangul_KiyeogSios
        { 0x0EA4, '\u3134' },  // XK_Hangul_Nieun
        { 0x0EA5, '\u3135' },  // XK_Hangul_NieunJieuj
        { 0x0EA6, '\u3136' },  // XK_Hangul_NieunHieuh
        { 0x0EA7, '\u3137' },  // XK_Hangul_Dikeud
        { 0x0EA8, '\u3138' },  // XK_Hangul_SsangDikeud
        { 0x0EA9, '\u3139' },  // XK_Hangul_Rieul
        { 0x0EAA, '\u313A' },  // XK_Hangul_RieulKiyeog
        { 0x0EAB, '\u313B' },  // XK_Hangul_RieulMieum
        { 0x0EAC, '\u313C' },  // XK_Hangul_RieulPieub
        { 0x0EAD, '\u313D' },  // XK_Hangul_RieulSios
        { 0x0EAE, '\u313E' },  // XK_Hangul_RieulTieut
        { 0x0EAF, '\u313F' },  // XK_Hangul_RieulPhieuf
        { 0x0EB0, '\u3140' },  // XK_Hangul_RieulHieuh
        { 0x0EB1, '\u3141' },  // XK_Hangul_Mieum
        { 0x0EB2, '\u3142' },  // XK_Hangul_Pieub
        { 0x0EB3, '\u3143' },  // XK_Hangul_SsangPieub
        { 0x0EB4, '\u3144' },  // XK_Hangul_PieubSios
        { 0x0EB5, '\u3145' },  // XK_Hangul_Sios
        { 0x0EB6, '\u3146' },  // XK_Hangul_SsangSios
        { 0x0EB7, '\u3147' },  // XK_Hangul_Ieung
        { 0x0EB8, '\u3148' },  // XK_Hangul_Jieuj
        { 0x0EB9, '\u3149' },  // XK_Hangul_SsangJieuj
        { 0x0EBA, '\u314A' },  // XK_Hangul_Cieuc
        { 0x0EBB, '\u314B' },  // XK_Hangul_Khieuq
        { 0x0EBC, '\u314C' },  // XK_Hangul_Tieut
        { 0x0EBD, '\u314D' },  // XK_Hangul_Phieuf
        { 0x0EBE, '\u314E' },  // XK_Hangul_Hieuh
        { 0x0EBF, '\u314F' },  // XK_Hangul_A
        { 0x0EC0, '\u3150' },  // XK_Hangul_AE
        { 0x0EC1, '\u3151' },  // XK_Hangul_YA
        { 0x0EC2, '\u3152' },  // XK_Hangul_YAE
        { 0x0EC3, '\u3153' },  // XK_Hangul_EO
        { 0x0EC4, '\u3154' },  // XK_Hangul_E
        { 0x0EC5, '\u3155' },  // XK_Hangul_YEO
        { 0x0EC6, '\u3156' },  // XK_Hangul_YE
        { 0x0EC7, '\u3157' },  // XK_Hangul_O
        { 0x0EC8, '\u3158' },  // XK_Hangul_WA
        { 0x0EC9, '\u3159' },  // XK_Hangul_WAE
        { 0x0ECA, '\u315A' },  // XK_Hangul_OE
        { 0x0ECB, '\u315B' },  // XK_Hangul_YO
        { 0x0ECC, '\u315C' },  // XK_Hangul_U
        { 0x0ECD, '\u315D' },  // XK_Hangul_WEO
        { 0x0ECE, '\u315E' },  // XK_Hangul_WE
        { 0x0ECF, '\u315F' },  // XK_Hangul_WI
        { 0x0ED0, '\u3160' },  // XK_Hangul_YU
        { 0x0ED1, '\u3161' },  // XK_Hangul_EU
        { 0x0ED2, '\u3162' },  // XK_Hangul_YI
        { 0x0ED3, '\u3163' },  // XK_Hangul_I
        { 0x0ED4, '\u11A8' },  // XK_Hangul_J_Kiyeog
        { 0x0ED5, '\u11A9' },  // XK_Hangul_J_SsangKiyeog
        { 0x0ED6, '\u11AA' },  // XK_Hangul_J_KiyeogSios
        { 0x0ED7, '\u11AB' },  // XK_Hangul_J_Nieun
        { 0x0ED8, '\u11AC' },  // XK_Hangul_J_NieunJieuj
        { 0x0ED9, '\u11AD' },  // XK_Hangul_J_NieunHieuh
        { 0x0EDA, '\u11AE' },  // XK_Hangul_J_Dikeud
        { 0x0EDB, '\u11AF' },  // XK_Hangul_J_Rieul
        { 0x0EDC, '\u11B0' },  // XK_Hangul_J_RieulKiyeog
        { 0x0EDD, '\u11B1' },  // XK_Hangul_J_RieulMieum
        { 0x0EDE, '\u11B2' },  // XK_Hangul_J_RieulPieub
        { 0x0EDF, '\u11B3' },  // XK_Hangul_J_RieulSios
        { 0x0EE0, '\u11B4' },  // XK_Hangul_J_RieulTieut
        { 0x0EE1, '\u11B5' },  // XK_Hangul_J_RieulPhieuf
        { 0x0EE2, '\u11B6' },  // XK_Hangul_J_RieulHieuh
        { 0x0EE3, '\u11B7' },  // XK_Hangul_J_Mieum
        { 0x0EE4, '\u11B8' },  // XK_Hangul_J_Pieub
        { 0x0EE5, '\u11B9' },  // XK_Hangul_J_PieubSios
        { 0x0EE6, '\u11BA' },  // XK_Hangul_J_Sios
        { 0x0EE7, '\u11BB' },  // XK_Hangul_J_SsangSios
        { 0x0EE8, '\u11BC' },  // XK_Hangul_J_Ieung
        { 0x0EE9, '\u11BD' },  // XK_Hangul_J_Jieuj
        { 0x0EEA, '\u11BE' },  // XK_Hangul_J_Cieuc
        { 0x0EEB, '\u11BF' },  // XK_Hangul_J_Khieuq
        { 0x0EEC, '\u11C0' },  // XK_Hangul_J_Tieut
        { 0x0EED, '\u11C1' },  // XK_Hangul_J_Phieuf
        { 0x0EEE, '\u11C2' },  // XK_Hangul_J_Hieuh
        { 0x0EEF, '\u316D' },  // XK_Hangul_RieulYeorinHieuh
        { 0x0EF0, '\u3171' },  // XK_Hangul_SunkyeongeumMieum
        { 0x0EF1, '\u3178' },  // XK_Hangul_SunkyeongeumPieub
        { 0x0EF2, '\u317F' },  // XK_Hangul_PanSios
        { 0x0EF3, '\u3181' },  // XK_Hangul_KkogjiDalrinIeung
        { 0x0EF4, '\u3184' },  // XK_Hangul_SunkyeongeumPhieuf
        { 0x0EF5, '\u3186' },  // XK_Hangul_YeorinHieuh
        { 0x0EF6, '\u318D' },  // XK_Hangul_AraeA
        { 0x0EF7, '\u318E' },  // XK_Hangul_AraeAE
        { 0x0EF8, '\u11EB' },  // XK_Hangul_J_PanSios
        { 0x0EF9, '\u11F0' },  // XK_Hangul_J_KkogjiDalrinIeung
        { 0x0EFA, '\u11F9' },  // XK_Hangul_J_YeorinHieuh
        { 0x0EFF, '\u20A9' },  // XK_Korean_Won

        // latin 8 / Celtic (XK_LATIN8, byte 3 = 12)
        { 0x12A1, '\u1E02' },  // XK_Babovedot    → Ḃ
        { 0x12A2, '\u1E03' },  // XK_babovedot    → ḃ
        { 0x12A3, '\u1E0A' },  // XK_Dabovedot    → Ḋ
        { 0x12A4, '\u1E80' },  // XK_Wgrave       → Ẁ
        { 0x12A5, '\u1E82' },  // XK_Wacute       → Ẃ
        { 0x12A6, '\u1E0B' },  // XK_dabovedot    → ḋ
        { 0x12A7, '\u1EF2' },  // XK_Ygrave       → Ỳ
        { 0x12A8, '\u1E1E' },  // XK_Fabovedot    → Ḟ
        { 0x12A9, '\u1E1F' },  // XK_fabovedot    → ḟ
        { 0x12AA, '\u1E40' },  // XK_Mabovedot    → Ṁ
        { 0x12AB, '\u1E41' },  // XK_mabovedot    → ṁ
        { 0x12AC, '\u1E56' },  // XK_Pabovedot    → Ṗ
        { 0x12AD, '\u1E81' },  // XK_wgrave       → ẁ
        { 0x12AE, '\u1E57' },  // XK_pabovedot    → ṗ
        { 0x12AF, '\u1E83' },  // XK_wacute       → ẃ
        { 0x12B0, '\u1E60' },  // XK_Sabovedot    → Ṡ
        { 0x12B1, '\u1EF3' },  // XK_ygrave       → ỳ
        { 0x12B2, '\u1E84' },  // XK_Wdiaeresis   → Ẅ
        { 0x12B3, '\u1E85' },  // XK_wdiaeresis   → ẅ
        { 0x12B4, '\u1E61' },  // XK_sabovedot    → ṡ
        { 0x12B5, '\u0174' },  // XK_Wcircumflex  → Ŵ
        { 0x12B6, '\u1E6A' },  // XK_Tabovedot    → Ṫ
        { 0x12B7, '\u0176' },  // XK_Ycircumflex  → Ŷ
        { 0x12B8, '\u0175' },  // XK_wcircumflex  → ŵ
        { 0x12B9, '\u1E6B' },  // XK_tabovedot    → ṫ
        { 0x12BA, '\u0177' },  // XK_ycircumflex  → ŷ

        // latin 9 extras (OE/oe/Ydiaeresis, 0x13xx)
        { 0x13BC, '\u0152' },  // XK_OE              → Œ
        { 0x13BD, '\u0153' },  // XK_oe              → œ
        { 0x13BE, '\u0178' },  // XK_Ydiaeresis      → Ÿ
    };

    // detects which X11 modifier slot holds Scroll_Lock (XK_Scroll_Lock = 0xFF14) and caches the mask.
    // must be called once after XOpenDisplay. safe to skip — _scrollLockMask defaults to Mod3.
    internal void Init(nint display)
    {
        _scrollLockMask = DetectScrollLockMask(display);
    }

    // walks XGetModifierMapping to find which Mod* slot contains the Scroll_Lock keysym.
    // Mod3 (0x20) is the conventional mapping but is not guaranteed on all desktop configurations.
    // returns 0 if Scroll_Lock is not bound to any modifier slot.
    private static uint DetectScrollLockMask(nint display)
    {
        const ulong xkScrollLock = 0xFF14;
        var modmap = NativeMethods.XGetModifierMapping(display);
        if (modmap == nint.Zero) return NativeMethods.Mod3Mask;
        try
        {
            // XModifierKeymap layout: int max_keypermod at offset 0, then (LP64) 4-byte pad + 8-byte pointer at offset 8.
            // on ILP32 (32-bit Linux) the pointer would be at offset 4 — this code targets linux-x64 (LP64) only.
            var maxPerMod = Marshal.ReadInt32(modmap, 0);
            var keycodePtr = Marshal.ReadIntPtr(modmap, 8);
            for (var slot = 0; slot < 8; slot++)
            {
                for (var k = 0; k < maxPerMod; k++)
                {
                    var keycode = Marshal.ReadByte(keycodePtr, slot * maxPerMod + k);
                    if (keycode == 0) continue;
                    if (NativeMethods.XkbKeycodeToKeysym(display, keycode, 0, 0) == xkScrollLock)
                        return (uint)(1 << slot);
                }
            }
            return 0;
        }
        finally
        {
            _ = NativeMethods.XFreeModifiermap(modmap);
        }
    }

    // X11 standard modifier mask bits (from X.h).
    // Mod1 = Alt, Mod2 = NumLock, Mod4 = Super, Mod5 = AltGr on standard desktop configurations.
    // ScrollLock mask is determined dynamically by Init() — defaults to Mod3 if not called.
    private KeyModifiers MapModifiers(uint state)
    {
        var mods = KeyModifiers.None;
        if ((state & NativeMethods.ShiftMask) != 0) mods |= KeyModifiers.Shift;
        if ((state & NativeMethods.LockMask) != 0) mods |= KeyModifiers.CapsLock;
        if ((state & NativeMethods.ControlMask) != 0) mods |= KeyModifiers.Control;
        if ((state & NativeMethods.Mod1Mask) != 0) mods |= KeyModifiers.Alt;
        if ((state & NativeMethods.Mod2Mask) != 0) mods |= KeyModifiers.NumLock;
        if (_scrollLockMask != 0 && (state & _scrollLockMask) != 0) mods |= KeyModifiers.ScrollLock;
        if ((state & NativeMethods.Mod4Mask) != 0) mods |= KeyModifiers.Super;
        if ((state & NativeMethods.Mod5Mask) != 0) mods |= KeyModifiers.AltGr;
        // AltGr and Alt are mutually exclusive: strip Alt when AltGr is active
        if ((mods & KeyModifiers.AltGr) != 0) mods &= ~KeyModifiers.Alt;
        return mods;
    }

    // resets resolver state when the keyboard grab is lost and re-established.
    // missed key-up events while ungrabbed leave stale entries in _keyDownId;
    // clearing prevents phantom stuck-key suppression on the next session.
    internal void Reset()
    {
        _pendingDeadKey = '\0';
        _pendingDeadSpacing = '\0';
        _keyDownId.Clear();
        _heldModifierCounts.Clear();
    }
}
