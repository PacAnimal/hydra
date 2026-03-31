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
    private readonly Dictionary<uint, (char? ch, SpecialKey? key)> _keyDownId = [];

    internal KeyEvent? Resolve(int evType, uint keycode, uint state, nint display)
    {
        // resolve using the active layout: group from Xkb state bits, level from Shift/AltGr
        var group = ExtractGroup(state);
        var level = ComputeLevel(state);
        var keysym = NativeMethods.XkbKeycodeToKeysym(display, keycode, group, level);

        // fall back to base keysym (0,0) for modifier keys and unmapped levels
        if (keysym == 0)
            keysym = NativeMethods.XkbKeycodeToKeysym(display, keycode, 0, 0);
        if (keysym == 0) return null;

        // X11 state lags by one: the modifier being pressed/released is not yet reflected.
        // adjust so modifiers always describe what's held after the event takes effect.
        var adjustedState = AdjustModifierState(evType, keysym, state);
        var mods = MapModifiers(adjustedState);
        var type = evType == NativeMethods.KeyPress ? KeyEventType.KeyDown : KeyEventType.KeyUp;

        // key-up: replay the same event that was emitted on key-down
        if (evType == NativeMethods.KeyRelease)
        {
            if (!_keyDownId.TryGetValue(keycode, out var downVal)) return null;
            _keyDownId.Remove(keycode);
            if (downVal.ch.HasValue) return KeyEvent.Char(type, downVal.ch.Value, mods);
            if (downVal.key.HasValue) return KeyEvent.Special(type, downVal.key.Value, mods);
            return null;
        }

        // dead key: store combining char and its spacing form, then wait for the next character
        var combining = DeadKeyCombining(keysym);
        if (combining != '\0')
        {
            _pendingDeadKey = combining;
            _pendingDeadSpacing = DeadKeySpacing(keysym);
            return null;
        }

        // special key (arrows, function keys, modifiers, keypad)?
        var special = KeySymToSpecialKey(keysym);
        if (special.HasValue)
        {
            _pendingDeadKey = '\0';
            _pendingDeadSpacing = '\0';
            _keyDownId[keycode] = (null, special.Value);
            return KeyEvent.Special(type, special.Value, mods);
        }

        // character key
        var ch = KeySymToChar(keysym);
        if (ch.HasValue)
        {
            if (_pendingDeadKey != '\0')
            {
                var dead = _pendingDeadKey;
                var spacing = _pendingDeadSpacing;
                _pendingDeadKey = '\0';
                _pendingDeadSpacing = '\0';

                if (ch.Value == ' ' && spacing != '\0')
                    ch = spacing;   // dead key + space → spacing form (e.g. dead_tilde + space → ~)
                else
                {
                    var composed = KeyResolver.Compose(ch.Value, dead);
                    if (composed != ch.Value) ch = composed;
                    // else: incompatible pair — emit base char, dead key lost
                }
            }
            _keyDownId[keycode] = (ch, null);
            return KeyEvent.Char(type, ch.Value, mods);
        }

        return null;
    }

    // returns the Unicode combining character for a dead keysym, or '\0' if not a dead key.
    // dead keysyms live in 0xFE50-0xFE5F (X11 keysymdef.h XK_dead_* constants).
    private static char DeadKeyCombining(ulong keysym) => keysym switch
    {
        0xFE50 => '\u0300',  // XK_dead_grave        → combining grave accent
        0xFE51 => '\u0301',  // XK_dead_acute        → combining acute accent
        0xFE52 => '\u0302',  // XK_dead_circumflex   → combining circumflex
        0xFE53 => '\u0303',  // XK_dead_tilde        → combining tilde
        0xFE54 => '\u0304',  // XK_dead_macron       → combining macron
        0xFE55 => '\u0306',  // XK_dead_breve        → combining breve
        0xFE56 => '\u0307',  // XK_dead_abovedot     → combining dot above
        0xFE57 => '\u0308',  // XK_dead_diaeresis    → combining diaeresis
        0xFE58 => '\u030A',  // XK_dead_abovering    → combining ring above
        0xFE59 => '\u030B',  // XK_dead_doubleacute  → combining double acute
        0xFE5A => '\u030C',  // XK_dead_caron        → combining caron
        0xFE5B => '\u0327',  // XK_dead_cedilla      → combining cedilla
        0xFE5C => '\u0328',  // XK_dead_ogonek       → combining ogonek
        0xFE5E => '\u0335',  // XK_dead_stroke       → combining short stroke
        _ => '\0',
    };

    // maps a dead keysym to its standalone spacing character (emitted when dead key + space is pressed).
    private static char DeadKeySpacing(ulong keysym) => keysym switch
    {
        0xFE50 => '\u0060',  // XK_dead_grave        → ` (grave accent)
        0xFE51 => '\u00B4',  // XK_dead_acute        → ´ (acute accent)
        0xFE52 => '\u005E',  // XK_dead_circumflex   → ^ (circumflex)
        0xFE53 => '\u007E',  // XK_dead_tilde        → ~ (tilde)
        0xFE54 => '\u00AF',  // XK_dead_macron       → ¯ (macron)
        0xFE55 => '\u02D8',  // XK_dead_breve        → ˘ (breve)
        0xFE56 => '\u02D9',  // XK_dead_abovedot     → ˙ (dot above)
        0xFE57 => '\u00A8',  // XK_dead_diaeresis    → ¨ (diaeresis)
        0xFE58 => '\u02DA',  // XK_dead_abovering    → ˚ (ring above)
        0xFE59 => '\u02DD',  // XK_dead_doubleacute  → ˝ (double acute)
        0xFE5A => '\u02C7',  // XK_dead_caron        → ˇ (caron)
        0xFE5B => '\u00B8',  // XK_dead_cedilla      → ¸ (cedilla)
        0xFE5C => '\u02DB',  // XK_dead_ogonek       → ˛ (ogonek)
        0xFE5E => '\u002F',  // XK_dead_stroke       → / (solidus)
        _ => '\0',
    };

    // checks special key map first, then falls back to mechanical MISCELLANY mapping.
    // MISCELLANY keysyms (0xFF00-0xFFFF) map directly: keysym | 0x01000000 = SpecialKey value.
    private static SpecialKey? KeySymToSpecialKey(ulong keysym)
    {
        if (XorgSpecialKeyMap.TryGet(keysym, out var special)) return special;

        // mechanical MISCELLANY mapping for named keys not in the special map
        if (keysym is >= 0xFF00 and <= 0xFFFF)
            return (SpecialKey)(keysym | 0x01000000);

        return null;
    }

    // maps a keysym to a printable char.
    // latin-1 printable range (0x0020-0x00FF) maps directly as unicode codepoints.
    // modern Unicode-extension keysyms (0x01000000 | codepoint) strip the flag bit.
    // legacy named keysyms where X11 value equals Unicode codepoint (e.g. EuroSign = 0x20AC).
    private static char? KeySymToChar(ulong keysym)
    {
        if (keysym is >= 0x0020 and <= 0x00FF)
            return (char)keysym;

        if (keysym is >= 0x01000100 and <= 0x0110FFFF)
            return (char)(keysym - 0x01000000);

        if (keysym is > 0x00FF and < 0xFF00)
            return (char)keysym;

        return null;
    }

    // Xkb group is encoded in bits 13-14 of the X11 state field (XkbGroupForCoreState macro)
    private static int ExtractGroup(uint state) => (int)((state >> 13) & 3);

    // shift level: 0=base, 1=Shift, 2=AltGr, 3=Shift+AltGr
    private static int ComputeLevel(uint state)
    {
        var shift = (state & NativeMethods.ShiftMask) != 0;
        var altGr = (state & NativeMethods.Mod5Mask) != 0;
        return (shift ? 1 : 0) + (altGr ? 2 : 0);
    }

    // X11 state field reflects modifier state before the event takes effect.
    // for modifier key presses: the bit isn't set yet — add it.
    // for modifier key releases: the bit is still set — remove it.
    private static uint AdjustModifierState(int evType, ulong keysym, uint state)
    {
        var bit = ModifierBit(keysym);
        if (bit == 0) return state;
        return evType == NativeMethods.KeyPress ? state | bit : state & ~bit;
    }

    // X11 modifier mask bit for a modifier keysym, or 0 for non-modifier keys
    private static uint ModifierBit(ulong keysym) => keysym switch
    {
        XorgVirtualKey.Shift_L or XorgVirtualKey.Shift_R => NativeMethods.ShiftMask,
        XorgVirtualKey.Control_L or XorgVirtualKey.Control_R => NativeMethods.ControlMask,
        XorgVirtualKey.Alt_L or XorgVirtualKey.Alt_R => NativeMethods.Mod1Mask,
        XorgVirtualKey.Super_L or XorgVirtualKey.Super_R => NativeMethods.Mod4Mask,
        XorgVirtualKey.CapsLock => NativeMethods.LockMask,
        XorgVirtualKey.NumLock => NativeMethods.Mod2Mask,
        XorgVirtualKey.ISO_Level3_Shift => NativeMethods.Mod5Mask,
        _ => 0,
    };

    // X11 standard modifier mask bits (from X.h).
    // Mod1 = Alt, Mod2 = NumLock, Mod4 = Super, Mod5 = AltGr on standard desktop configurations.
    private static KeyModifiers MapModifiers(uint state)
    {
        var mods = KeyModifiers.None;
        if ((state & NativeMethods.ShiftMask) != 0) mods |= KeyModifiers.Shift;
        if ((state & NativeMethods.LockMask) != 0) mods |= KeyModifiers.CapsLock;
        if ((state & NativeMethods.ControlMask) != 0) mods |= KeyModifiers.Control;
        if ((state & NativeMethods.Mod1Mask) != 0) mods |= KeyModifiers.Alt;
        if ((state & NativeMethods.Mod2Mask) != 0) mods |= KeyModifiers.NumLock;
        if ((state & NativeMethods.Mod4Mask) != 0) mods |= KeyModifiers.Super;
        if ((state & NativeMethods.Mod5Mask) != 0) mods |= KeyModifiers.AltGr;
        return mods;
    }
}
