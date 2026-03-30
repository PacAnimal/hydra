using Hydra.Keyboard;

namespace Hydra.Platform.Linux;

// translates X11 key events into platform-independent KeyEvents.
// always resolves the base keysym (group 0, level 0) regardless of modifier state,
// matching the Mac/Windows approach of using the key's identity rather than its output character.
internal sealed class XorgKeyResolver
{
    internal static KeyEvent? Resolve(int evType, uint keycode, uint state, nint display)
    {
        var keysym = NativeMethods.XkbKeycodeToKeysym(display, keycode, 0, 0);
        if (keysym == 0) return null;

        // X11 state lags by one: the modifier being pressed/released is not yet reflected.
        // adjust so modifiers always describe what's held after the event takes effect.
        state = AdjustModifierState(evType, keysym, state);

        var mods = MapModifiers(state);
        var type = evType == NativeMethods.KeyPress ? KeyEventType.KeyDown : KeyEventType.KeyUp;
        var keyId = KeySymToKeyId(keysym);
        if (keyId == KeyId.None) return null;

        return new KeyEvent(type, keyId, mods, (ushort)keycode);
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
