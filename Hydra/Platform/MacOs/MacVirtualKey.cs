namespace Hydra.Platform.MacOs;

// macOS virtual key codes (kVK_* from Carbon Events.h)
internal static class MacVirtualKey
{
    // modifiers
    internal const ulong Shift = 0x38;
    internal const ulong RightShift = 0x3C;
    internal const ulong Control = 0x3B;
    internal const ulong RightControl = 0x3E;
    internal const ulong Option = 0x3A;
    internal const ulong RightOption = 0x3D;
    internal const ulong Command = 0x37;
    internal const ulong RightCommand = 0x36;
    internal const ulong CapsLock = 0x39;

    // navigation
    internal const ulong Return = 0x24;
    internal const ulong Tab = 0x30;
    internal const ulong Delete = 0x33;       // backspace
    internal const ulong Escape = 0x35;
    internal const ulong ForwardDelete = 0x75;
    internal const ulong Home = 0x73;
    internal const ulong End = 0x77;
    internal const ulong PageUp = 0x74;
    internal const ulong PageDown = 0x79;
    internal const ulong Help = 0x72;         // insert on non-apple keyboards
    internal const ulong LeftArrow = 0x7B;
    internal const ulong RightArrow = 0x7C;
    internal const ulong DownArrow = 0x7D;
    internal const ulong UpArrow = 0x7E;

    // function keys
    internal const ulong F1 = 0x7A;
    internal const ulong F2 = 0x78;
    internal const ulong F3 = 0x63;
    internal const ulong F4 = 0x76;
    internal const ulong F5 = 0x60;
    internal const ulong F6 = 0x61;
    internal const ulong F7 = 0x62;
    internal const ulong F8 = 0x64;
    internal const ulong F9 = 0x65;
    internal const ulong F10 = 0x6D;
    internal const ulong F11 = 0x67;
    internal const ulong F12 = 0x6F;
    internal const ulong F13 = 0x69;
    internal const ulong F14 = 0x6B;
    internal const ulong F15 = 0x71;
    internal const ulong F16 = 0x6A;
    internal const ulong F17 = 0x40;
    internal const ulong F18 = 0x4F;
    internal const ulong F19 = 0x50;
    internal const ulong F20 = 0x5A;

    // keypad
    internal const ulong KeypadClear = 0x47;  // numlock on mac
    internal const ulong KeypadEnter = 0x4C;
    internal const ulong KeypadDecimal = 0x41;
    internal const ulong KeypadMultiply = 0x43;
    internal const ulong KeypadPlus = 0x45;
    internal const ulong KeypadDivide = 0x4B;
    internal const ulong KeypadMinus = 0x4E;
    internal const ulong KeypadEquals = 0x51;
    internal const ulong Keypad0 = 0x52;
    internal const ulong Keypad1 = 0x53;
    internal const ulong Keypad2 = 0x54;
    internal const ulong Keypad3 = 0x55;
    internal const ulong Keypad4 = 0x56;
    internal const ulong Keypad5 = 0x57;
    internal const ulong Keypad6 = 0x58;
    internal const ulong Keypad7 = 0x59;
    internal const ulong Keypad8 = 0x5B;
    internal const ulong Keypad9 = 0x5C;

    // media / misc
    internal const ulong VolumeUp = 0x48;
    internal const ulong VolumeDown = 0x49;
    internal const ulong Mute = 0x4A;
}
