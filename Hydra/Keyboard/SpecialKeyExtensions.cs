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
}
