namespace Hydra.Platform.MacOs;

// macOS virtual key codes (kVK_* from Carbon Events.h)
internal static class MacVirtualKey
{
    // modifiers
    internal const int Shift = 0x38;
    internal const int RightShift = 0x3C;
    internal const int Control = 0x3B;
    internal const int RightControl = 0x3E;
    internal const int Option = 0x3A;
    internal const int RightOption = 0x3D;
    internal const int Command = 0x37;
    internal const int RightCommand = 0x36;
    internal const int CapsLock = 0x39;

    // navigation
    internal const int Return = 0x24;
    internal const int Tab = 0x30;
    internal const int Delete = 0x33;       // backspace
    internal const int Escape = 0x35;
    internal const int ForwardDelete = 0x75;
    internal const int Home = 0x73;
    internal const int End = 0x77;
    internal const int PageUp = 0x74;
    internal const int PageDown = 0x79;
    internal const int Help = 0x72;         // insert on non-apple keyboards
    internal const int LeftArrow = 0x7B;
    internal const int RightArrow = 0x7C;
    internal const int DownArrow = 0x7D;
    internal const int UpArrow = 0x7E;

    // function keys
    internal const int F1 = 0x7A;
    internal const int F2 = 0x78;
    internal const int F3 = 0x63;
    internal const int F4 = 0x76;
    internal const int F5 = 0x60;
    internal const int F6 = 0x61;
    internal const int F7 = 0x62;
    internal const int F8 = 0x64;
    internal const int F9 = 0x65;
    internal const int F10 = 0x6D;
    internal const int F11 = 0x67;
    internal const int F12 = 0x6F;
    internal const int F13 = 0x69;
    internal const int F14 = 0x6B;
    internal const int F15 = 0x71;
    internal const int F16 = 0x6A;
    internal const int F17 = 0x40;
    internal const int F18 = 0x4F;
    internal const int F19 = 0x50;
    internal const int F20 = 0x5A;

    // keypad
    internal const int KeypadClear = 0x47;  // numlock on mac
    internal const int KeypadEnter = 0x4C;
    internal const int KeypadDecimal = 0x41;
    internal const int KeypadMultiply = 0x43;
    internal const int KeypadPlus = 0x45;
    internal const int KeypadDivide = 0x4B;
    internal const int KeypadMinus = 0x4E;
    internal const int KeypadEquals = 0x51;
    internal const int Keypad0 = 0x52;
    internal const int Keypad1 = 0x53;
    internal const int Keypad2 = 0x54;
    internal const int Keypad3 = 0x55;
    internal const int Keypad4 = 0x56;
    internal const int Keypad5 = 0x57;
    internal const int Keypad6 = 0x58;
    internal const int Keypad7 = 0x59;
    internal const int Keypad8 = 0x5B;
    internal const int Keypad9 = 0x5C;

    // media / misc
    internal const int VolumeUp = 0x48;
    internal const int VolumeDown = 0x49;
    internal const int Mute = 0x4A;
}
