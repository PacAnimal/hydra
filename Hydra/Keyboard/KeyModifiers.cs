namespace Hydra.Keyboard;

[Flags]
public enum KeyModifiers : uint
{
    None = 0,
    Shift = 0x0001,
    Control = 0x0002,
    Alt = 0x0004,
    Meta = 0x0008,
    Super = 0x0010,
    AltGr = 0x0020,
    CapsLock = 0x1000,
    NumLock = 0x2000,
    ScrollLock = 0x4000,
}
