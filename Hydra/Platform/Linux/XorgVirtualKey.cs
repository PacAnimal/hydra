// ReSharper disable InconsistentNaming
namespace Hydra.Platform.Linux;

// X11 keysym constants (XK_* values from <X11/keysymdef.h>).
// note: Hydra's KeyId constants are derived from these by subtracting 0x1000,
// so XorgSpecialKeyMap entries are purely for clarity and validation.
internal static class XorgVirtualKey
{
    // tty
    internal const ulong BackSpace = 0xFF08;
    internal const ulong Tab = 0xFF09;
    internal const ulong Return = 0xFF0D;
    internal const ulong Escape = 0xFF1B;
    internal const ulong Delete = 0xFFFF;

    // cursor / navigation
    internal const ulong Home = 0xFF50;
    internal const ulong Left = 0xFF51;
    internal const ulong Up = 0xFF52;
    internal const ulong Right = 0xFF53;
    internal const ulong Down = 0xFF54;
    internal const ulong PageUp = 0xFF55;
    internal const ulong PageDown = 0xFF56;
    internal const ulong End = 0xFF57;
    internal const ulong Insert = 0xFF63;

    // misc
    internal const ulong NumLock = 0xFF7F;
    internal const ulong ScrollLock = 0xFF14;

    // keypad
    internal const ulong KP_Space = 0xFF80;
    internal const ulong KP_Tab = 0xFF89;
    internal const ulong KP_Enter = 0xFF8D;
    internal const ulong KP_Equal = 0xFFBD;
    internal const ulong KP_Multiply = 0xFFAA;
    internal const ulong KP_Add = 0xFFAB;
    internal const ulong KP_Subtract = 0xFFAD;
    internal const ulong KP_Decimal = 0xFFAE;
    internal const ulong KP_Divide = 0xFFAF;
    internal const ulong KP_0 = 0xFFB0;
    internal const ulong KP_1 = 0xFFB1;
    internal const ulong KP_2 = 0xFFB2;
    internal const ulong KP_3 = 0xFFB3;
    internal const ulong KP_4 = 0xFFB4;
    internal const ulong KP_5 = 0xFFB5;
    internal const ulong KP_6 = 0xFFB6;
    internal const ulong KP_7 = 0xFFB7;
    internal const ulong KP_8 = 0xFFB8;
    internal const ulong KP_9 = 0xFFB9;

    // function keys
    internal const ulong F1 = 0xFFBE;
    internal const ulong F2 = 0xFFBF;
    internal const ulong F3 = 0xFFC0;
    internal const ulong F4 = 0xFFC1;
    internal const ulong F5 = 0xFFC2;
    internal const ulong F6 = 0xFFC3;
    internal const ulong F7 = 0xFFC4;
    internal const ulong F8 = 0xFFC5;
    internal const ulong F9 = 0xFFC6;
    internal const ulong F10 = 0xFFC7;
    internal const ulong F11 = 0xFFC8;
    internal const ulong F12 = 0xFFC9;
    internal const ulong F13 = 0xFFCA;
    internal const ulong F14 = 0xFFCB;
    internal const ulong F15 = 0xFFCC;
    internal const ulong F16 = 0xFFCD;
    internal const ulong F17 = 0xFFCE;
    internal const ulong F18 = 0xFFCF;
    internal const ulong F19 = 0xFFD0;
    internal const ulong F20 = 0xFFD1;

    // media keys (XF86 vendor keysyms, 0x1008FFxx range)
    internal const ulong XF86AudioLowerVolume = 0x1008FF11;
    internal const ulong XF86AudioMute = 0x1008FF12;
    internal const ulong XF86AudioRaiseVolume = 0x1008FF13;
    internal const ulong XF86AudioPlay = 0x1008FF14;
    internal const ulong XF86AudioStop = 0x1008FF15;
    internal const ulong XF86AudioPrev = 0x1008FF16;
    internal const ulong XF86AudioNext = 0x1008FF17;
    internal const ulong XF86MonBrightnessUp = 0x1008FF02;
    internal const ulong XF86MonBrightnessDown = 0x1008FF03;
    internal const ulong XF86Eject = 0x1008FF2C;

    // modifiers
    internal const ulong ISO_Level3_Shift = 0xFE03;  // AltGr on most layouts
    internal const ulong Shift_L = 0xFFE1;
    internal const ulong Shift_R = 0xFFE2;
    internal const ulong Control_L = 0xFFE3;
    internal const ulong Control_R = 0xFFE4;
    internal const ulong CapsLock = 0xFFE5;
    internal const ulong Alt_L = 0xFFE9;
    internal const ulong Alt_R = 0xFFEA;
    internal const ulong Super_L = 0xFFEB;
    internal const ulong Super_R = 0xFFEC;
}
