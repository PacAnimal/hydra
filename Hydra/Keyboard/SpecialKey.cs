// ReSharper disable InconsistentNaming
namespace Hydra.Keyboard;

// named identifiers for non-printable keys.
// backing values encode the X11 MISCELLANY keysym with a flag bit: (keysym | 0x01000000).
// this keeps all values above the Unicode maximum (0x10FFFF), making them easy to
// distinguish from char/unicode codepoints. on Linux, a MISCELLANY keysym maps
// mechanically: (SpecialKey)(keysym | 0x01000000).
public enum SpecialKey : uint
{
    // tty
    BackSpace = 0x01FF08,
    Tab = 0x01FF09,
    Return = 0x01FF0D,
    Escape = 0x01FF1B,
    Delete = 0x01FFFF,

    // cursor / navigation
    Home = 0x01FF50,
    Left = 0x01FF51,
    Up = 0x01FF52,
    Right = 0x01FF53,
    Down = 0x01FF54,
    PageUp = 0x01FF55,
    PageDown = 0x01FF56,
    End = 0x01FF57,
    Insert = 0x01FF63,

    // misc
    AltGr = 0x01FE03,    // ISO_Level3_Shift (keysym 0xFE03 | 0x01000000)
    NumLock = 0x01FF7F,
    ScrollLock = 0x01FF14,

    // keypad
    KP_Space = 0x01FF80,
    KP_Tab = 0x01FF89,
    KP_Enter = 0x01FF8D,
    KP_Equal = 0x01FFBD,
    KP_Multiply = 0x01FFAA,
    KP_Add = 0x01FFAB,
    KP_Subtract = 0x01FFAD,
    KP_Decimal = 0x01FFAE,
    KP_Divide = 0x01FFAF,
    KP_0 = 0x01FFB0,
    KP_1 = 0x01FFB1,
    KP_2 = 0x01FFB2,
    KP_3 = 0x01FFB3,
    KP_4 = 0x01FFB4,
    KP_5 = 0x01FFB5,
    KP_6 = 0x01FFB6,
    KP_7 = 0x01FFB7,
    KP_8 = 0x01FFB8,
    KP_9 = 0x01FFB9,

    // function keys
    F1 = 0x01FFBE,
    F2 = 0x01FFBF,
    F3 = 0x01FFC0,
    F4 = 0x01FFC1,
    F5 = 0x01FFC2,
    F6 = 0x01FFC3,
    F7 = 0x01FFC4,
    F8 = 0x01FFC5,
    F9 = 0x01FFC6,
    F10 = 0x01FFC7,
    F11 = 0x01FFC8,
    F12 = 0x01FFC9,
    F13 = 0x01FFCA,
    F14 = 0x01FFCB,
    F15 = 0x01FFCC,
    F16 = 0x01FFCD,
    F17 = 0x01FFCE,
    F18 = 0x01FFCF,
    F19 = 0x01FFD0,
    F20 = 0x01FFD1,

    // platform actions (no X11/OS equivalent — synthesized per platform)
    MissionControl = 0xE100,

    // media
    AudioMute = 0xE0AD,
    AudioVolumeDown = 0xE0AE,
    AudioVolumeUp = 0xE0AF,
    AudioNext = 0xE0B0,
    AudioPrev = 0xE0B1,
    AudioStop = 0xE0B2,
    AudioPlay = 0xE0B3,
    BrightnessDown = 0xE0B8,
    BrightnessUp = 0xE0B9,
    Eject = 0xE001,

    // modifiers
    Shift_L = 0x01FFE1,
    Shift_R = 0x01FFE2,
    Control_L = 0x01FFE3,
    Control_R = 0x01FFE4,
    CapsLock = 0x01FFE5,
    Alt_L = 0x01FFE9,
    Alt_R = 0x01FFEA,
    Super_L = 0x01FFEB,
    Super_R = 0x01FFEC,
}

public static class SpecialKeyExtensions
{
    // true for keys that modify subsequent input: shift, ctrl, alt, super, capslock, altgr
    public static bool IsModifier(this SpecialKey key) => key is
        SpecialKey.AltGr or SpecialKey.CapsLock or
        SpecialKey.Shift_L or SpecialKey.Shift_R or
        SpecialKey.Control_L or SpecialKey.Control_R or
        SpecialKey.Alt_L or SpecialKey.Alt_R or
        SpecialKey.Super_L or SpecialKey.Super_R;
}
