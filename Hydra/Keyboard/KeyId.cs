namespace Hydra.Keyboard;

// key symbol identifiers — utf-32 encoding.
// printable characters use their unicode codepoint.
// special keys use the private-use area 0xE000-0xEFFF (x11 keysym-derived).
public static class KeyId
{
    public const uint None = 0x0000;

    // tty
    public const uint BackSpace = 0xEF08;
    public const uint Tab = 0xEF09;
    public const uint Return = 0xEF0D;
    public const uint Escape = 0xEF1B;
    public const uint Delete = 0xEFFF;

    // cursor
    public const uint Home = 0xEF50;
    public const uint Left = 0xEF51;
    public const uint Up = 0xEF52;
    public const uint Right = 0xEF53;
    public const uint Down = 0xEF54;
    public const uint PageUp = 0xEF55;
    public const uint PageDown = 0xEF56;
    public const uint End = 0xEF57;

    // misc
    public const uint Insert = 0xEF63;
    public const uint AltGr = 0xEF7E;
    public const uint NumLock = 0xEF7F;

    // keypad
    public const uint KP_Space = 0xEF80;
    public const uint KP_Tab = 0xEF89;
    public const uint KP_Enter = 0xEF8D;
    public const uint KP_Equal = 0xEFBD;
    public const uint KP_Multiply = 0xEFAA;
    public const uint KP_Add = 0xEFAB;
    public const uint KP_Subtract = 0xEFAD;
    public const uint KP_Decimal = 0xEFAE;
    public const uint KP_Divide = 0xEFAF;
    public const uint KP_0 = 0xEFB0;
    public const uint KP_1 = 0xEFB1;
    public const uint KP_2 = 0xEFB2;
    public const uint KP_3 = 0xEFB3;
    public const uint KP_4 = 0xEFB4;
    public const uint KP_5 = 0xEFB5;
    public const uint KP_6 = 0xEFB6;
    public const uint KP_7 = 0xEFB7;
    public const uint KP_8 = 0xEFB8;
    public const uint KP_9 = 0xEFB9;

    // function keys
    public const uint F1 = 0xEFBE;
    public const uint F2 = 0xEFBF;
    public const uint F3 = 0xEFC0;
    public const uint F4 = 0xEFC1;
    public const uint F5 = 0xEFC2;
    public const uint F6 = 0xEFC3;
    public const uint F7 = 0xEFC4;
    public const uint F8 = 0xEFC5;
    public const uint F9 = 0xEFC6;
    public const uint F10 = 0xEFC7;
    public const uint F11 = 0xEFC8;
    public const uint F12 = 0xEFC9;
    public const uint F13 = 0xEFCA;
    public const uint F14 = 0xEFCB;
    public const uint F15 = 0xEFCC;
    public const uint F16 = 0xEFCD;

    // modifiers
    public const uint Shift_L = 0xEFE1;
    public const uint Shift_R = 0xEFE2;
    public const uint Control_L = 0xEFE3;
    public const uint Control_R = 0xEFE4;
    public const uint CapsLock = 0xEFE5;
    public const uint Alt_L = 0xEFE9;
    public const uint Alt_R = 0xEFEA;
    public const uint Super_L = 0xEFEB;
    public const uint Super_R = 0xEFEC;

    // a printable glyph is: not none, and either outside the special range, or a keypad digit/equal
    public static bool IsPrintable(uint id) =>
        id != None && (id < 0xE000u || id > 0xEFFFu || IsKpCharacter(id));

    // special keys live in 0xE000-0xEFFF but are not the keypad character range
    public static bool IsSpecial(uint id) => id >= 0xE000u && id <= 0xEFFFu && !IsKpCharacter(id);

    private static bool IsKpCharacter(uint id) => (id >= KP_0 && id <= KP_9) || id == KP_Equal;

    public static bool IsModifier(uint id) => id >= Shift_L && id <= 0xEFEEu;
}
