namespace Hydra.Keyboard;

public static class SpecialKeyExtensions
{
    // true for keys that modify subsequent input: shift, ctrl, alt, super, capslock, altgr, numlock, scrolllock
    public static bool IsModifier(this SpecialKey key) => key is
        SpecialKey.AltGr or SpecialKey.CapsLock or SpecialKey.NumLock or SpecialKey.ScrollLock or
        SpecialKey.Shift_L or SpecialKey.Shift_R or
        SpecialKey.Control_L or SpecialKey.Control_R or
        SpecialKey.Alt_L or SpecialKey.Alt_R or
        SpecialKey.Super_L or SpecialKey.Super_R;

    // a numlock-off keypad key and the dedicated navigation key it stands for (KP_Begin has none)
    private static readonly Dictionary<SpecialKey, SpecialKey> KeypadNavigation = new()
    {
        { SpecialKey.KP_Home, SpecialKey.Home },
        { SpecialKey.KP_Left, SpecialKey.Left },
        { SpecialKey.KP_Up, SpecialKey.Up },
        { SpecialKey.KP_Right, SpecialKey.Right },
        { SpecialKey.KP_Down, SpecialKey.Down },
        { SpecialKey.KP_PageUp, SpecialKey.PageUp },
        { SpecialKey.KP_PageDown, SpecialKey.PageDown },
        { SpecialKey.KP_End, SpecialKey.End },
        { SpecialKey.KP_Insert, SpecialKey.Insert },
        { SpecialKey.KP_Delete, SpecialKey.Delete },
    };

    private static readonly Dictionary<SpecialKey, SpecialKey> KeypadTwins = KeypadNavigation.ToDictionary(kv => kv.Value, kv => kv.Key);

    // the dedicated key a keypad navigation key stands for (KP_Left → Left), else the key itself
    public static SpecialKey WithoutKeypad(this SpecialKey key) => KeypadNavigation.GetValueOrDefault(key, key);

    // the keypad navigation key standing for a dedicated one (Left → KP_Left), else null
    public static SpecialKey? KeypadTwin(this SpecialKey key) => KeypadTwins.TryGetValue(key, out var twin) ? twin : null;

    // the keypad keys NumLock changes, the only ones a slave syncs NumLock for (Synergy's "not required" lock):
    // a macOS master flags every keypad key, arrows too, as NumLock (the numeric pad flag), and holds no lock to sync
    public static bool IsNumLockSensitive(this SpecialKey key) =>
        key.IsKeypadNumeric() || key == SpecialKey.KP_Begin || KeypadNavigation.ContainsKey(key);

    // what a keypad key types with NumLock on
    public static bool IsKeypadNumeric(this SpecialKey key) => key is >= SpecialKey.KP_0 and <= SpecialKey.KP_9 or SpecialKey.KP_Decimal;

    // a keypad key's digit and its navigation key, the two things it types with NumLock on and off
    private static readonly Dictionary<SpecialKey, SpecialKey> KeypadDigitToNavigation = new()
    {
        { SpecialKey.KP_0, SpecialKey.KP_Insert },
        { SpecialKey.KP_1, SpecialKey.KP_End },
        { SpecialKey.KP_2, SpecialKey.KP_Down },
        { SpecialKey.KP_3, SpecialKey.KP_PageDown },
        { SpecialKey.KP_4, SpecialKey.KP_Left },
        { SpecialKey.KP_5, SpecialKey.KP_Begin },
        { SpecialKey.KP_6, SpecialKey.KP_Right },
        { SpecialKey.KP_7, SpecialKey.KP_Home },
        { SpecialKey.KP_8, SpecialKey.KP_Up },
        { SpecialKey.KP_9, SpecialKey.KP_PageUp },
        { SpecialKey.KP_Decimal, SpecialKey.KP_Delete },
    };

    private static readonly Dictionary<SpecialKey, SpecialKey> KeypadNavigationToDigit = KeypadDigitToNavigation.ToDictionary(kv => kv.Value, kv => kv.Key);

    // what a keypad key types with NumLock on (KP_Left → KP_4) or off (KP_4 → KP_Left); any other key is itself
    public static SpecialKey WithNumLock(this SpecialKey key, bool numLock) =>
        (numLock ? KeypadNavigationToDigit : KeypadDigitToNavigation).GetValueOrDefault(key, key);
}
