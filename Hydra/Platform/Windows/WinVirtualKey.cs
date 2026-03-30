namespace Hydra.Platform.Windows;

// Windows virtual key codes (VK_* from WinUser.h)
internal static class WinVirtualKey
{
    // editing / control
    internal const int Back = 0x08;        // backspace
    internal const int Tab = 0x09;
    internal const int Return = 0x0D;
    internal const int Escape = 0x1B;
    internal const int Space = 0x20;

    // navigation
    internal const int Prior = 0x21;       // page up
    internal const int Next = 0x22;        // page down
    internal const int End = 0x23;
    internal const int Home = 0x24;
    internal const int Left = 0x25;
    internal const int Up = 0x26;
    internal const int Right = 0x27;
    internal const int Down = 0x28;
    internal const int Insert = 0x2D;
    internal const int Delete = 0x2E;

    // windows keys
    internal const int LWin = 0x5B;
    internal const int RWin = 0x5C;

    // numpad
    internal const int Numpad0 = 0x60;
    internal const int Numpad1 = 0x61;
    internal const int Numpad2 = 0x62;
    internal const int Numpad3 = 0x63;
    internal const int Numpad4 = 0x64;
    internal const int Numpad5 = 0x65;
    internal const int Numpad6 = 0x66;
    internal const int Numpad7 = 0x67;
    internal const int Numpad8 = 0x68;
    internal const int Numpad9 = 0x69;
    internal const int Multiply = 0x6A;
    internal const int Add = 0x6B;
    internal const int Subtract = 0x6D;
    internal const int Decimal = 0x6E;
    internal const int Divide = 0x6F;

    // function keys
    internal const int F1 = 0x70;
    internal const int F2 = 0x71;
    internal const int F3 = 0x72;
    internal const int F4 = 0x73;
    internal const int F5 = 0x74;
    internal const int F6 = 0x75;
    internal const int F7 = 0x76;
    internal const int F8 = 0x77;
    internal const int F9 = 0x78;
    internal const int F10 = 0x79;
    internal const int F11 = 0x7A;
    internal const int F12 = 0x7B;
    internal const int F13 = 0x7C;
    internal const int F14 = 0x7D;
    internal const int F15 = 0x7E;
    internal const int F16 = 0x7F;

    // lock keys
    internal const int Capital = 0x14;     // caps lock
    internal const int Numlock = 0x90;
    internal const int Scroll = 0x91;      // scroll lock

    // modifiers — left and right variants
    internal const int LShift = 0xA0;
    internal const int RShift = 0xA1;
    internal const int LControl = 0xA2;
    internal const int RControl = 0xA3;
    internal const int LMenu = 0xA4;       // left alt
    internal const int RMenu = 0xA5;       // right alt / AltGr

    // generic modifier VKs (used for key state array indexing)
    internal const int Shift = 0x10;
    internal const int Control = 0x11;
    internal const int Menu = 0x12;        // alt

    // numpad enter: there is no distinct VK for numpad enter — VK_RETURN with extended flag
    internal const int Separator = 0x6C;   // numpad separator (not numpad enter on most layouts)
}
