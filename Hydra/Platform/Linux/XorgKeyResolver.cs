using System.Text;
using Hydra.Keyboard;

namespace Hydra.Platform.Linux;

// translates X11 key events into platform-independent KeyEvents.
// resolves the layout-translated character using the active keyboard layout and modifier state,
// matching the Mac approach of using the server-side layout to determine the intended character.
// instance-based to maintain dead key composition state across events.
internal sealed class XorgKeyResolver
{
    private char _pendingDeadKey;
    private readonly Dictionary<uint, uint> _keyDownId = [];  // keycode → last emitted KeyId

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

        var keyId = KeySymToKeyId(keysym);

        if (evType == NativeMethods.KeyPress)
        {
            var combining = DeadKeyCombining(keysym);
            if (combining != '\0')
            {
                // dead key: store combining char and wait for the next character
                _pendingDeadKey = combining;
                return null;
            }

            if (_pendingDeadKey != '\0')
            {
                if (KeyId.IsPrintable(keyId))
                    keyId = Compose(keyId);
                _pendingDeadKey = '\0';
            }
        }

        if (evType == NativeMethods.KeyPress)
        {
            if (keyId != KeyId.None)
                _keyDownId[keycode] = keyId;
        }
        else if (_keyDownId.TryGetValue(keycode, out var downId))
        {
            // use the same keyId that was emitted on KeyDown (e.g. 'é' not 'e' for composed keys)
            keyId = downId;
            _keyDownId.Remove(keycode);
        }

        if (keyId == KeyId.None) return null;

        return new KeyEvent(type, keyId, mods, (ushort)keycode);
    }

    // compose a pending dead key combining character with a base character via NFC normalization.
    // if composition produces no single codepoint (incompatible pair), returns the base unchanged.
    private uint Compose(uint baseKeyId)
    {
        if (baseKeyId > 0xFFFF) return baseKeyId;
        var composed = new string([(char)baseKeyId, _pendingDeadKey]).Normalize(NormalizationForm.FormC);
        return composed.Length == 1 ? composed[0] : baseKeyId;
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

    // Xkb group is encoded in bits 13-14 of the X11 state field (XkbGroupForCoreState macro)
    private static int ExtractGroup(uint state) => (int)((state >> 13) & 3);

    // shift level: 0=base, 1=Shift, 2=AltGr, 3=Shift+AltGr
    private static int ComputeLevel(uint state)
    {
        var shift = (state & NativeMethods.ShiftMask) != 0;
        var altGr = (state & NativeMethods.Mod5Mask) != 0;
        return (shift ? 1 : 0) + (altGr ? 2 : 0);
    }

    // maps a keysym to a KeyId.
    // special keys are looked up by table; MISCELLANY range (0xFF00-0xFFFF) maps mechanically
    // since Hydra's KeyId constants are literally keysym - 0x1000.
    // latin-1 printable range (0x0020-0x00FF) maps directly as unicode codepoints.
    private static uint KeySymToKeyId(ulong keysym)
    {
        if (XorgSpecialKeyMap.TryGet(keysym, out var specialId))
            return specialId;

        // latin-1 printable range: direct unicode codepoint
        if (keysym is >= 0x0020 and <= 0x00FF)
            return (uint)keysym;

        // MISCELLANY range: keysym - 0x1000 = KeyId (e.g. 0xFF51 → 0xEF51 = KeyId.Left)
        if (keysym is >= 0xFF00 and <= 0xFFFF)
            return (uint)(keysym - 0x1000);

        // modern Unicode-extension keysyms (0x01000000 | codepoint) — used by current XKB layouts
        if (keysym is >= 0x01000100 and <= 0x0110FFFF)
            return (uint)(keysym - 0x01000000);

        // legacy named keysyms where X11 value equals Unicode codepoint (e.g. EuroSign = 0x20AC)
        if (keysym is > 0x00FF and < 0xFF00)
            return (uint)keysym;

        return KeyId.None;
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
