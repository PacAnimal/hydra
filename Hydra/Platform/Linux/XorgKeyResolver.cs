using Hydra.Keyboard;

namespace Hydra.Platform.Linux;

// translates X11 XI2 raw key events into platform-independent KeyEvents.
// always resolves the base keysym (group 0, level 0) regardless of modifier state,
// matching the Mac/Windows approach of using the key's identity rather than its output character.
internal sealed class XorgKeyResolver
{
    internal static KeyEvent? Resolve(int evType, uint keycode, nint display)
    {
        var keysym = NativeMethods.XkbKeycodeToKeysym(display, keycode, 0, 0);
        if (keysym == 0) return null;

        var mods = ReadModifiers(display);
        var type = evType == NativeMethods.XI_RawKeyPress ? KeyEventType.KeyDown : KeyEventType.KeyUp;
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

    // X11 standard modifier mask bits (from X.h).
    // Mod1 = Alt, Mod2 = NumLock, Mod4 = Super on standard desktop configurations.
    private static KeyModifiers ReadModifiers(nint display)
    {
        NativeMethods.XkbGetState(display, NativeMethods.XkbUseCoreKbd, out var state);
        var m = state.Mods;
        var mods = KeyModifiers.None;
        if ((m & 0x01) != 0) mods |= KeyModifiers.Shift;      // ShiftMask
        if ((m & 0x02) != 0) mods |= KeyModifiers.CapsLock;   // LockMask
        if ((m & 0x04) != 0) mods |= KeyModifiers.Control;    // ControlMask
        if ((m & 0x08) != 0) mods |= KeyModifiers.Alt;        // Mod1Mask (typically Alt)
        if ((m & 0x10) != 0) mods |= KeyModifiers.NumLock;    // Mod2Mask (typically NumLock)
        if ((m & 0x40) != 0) mods |= KeyModifiers.Super;      // Mod4Mask (typically Super/Win)
        return mods;
    }
}
