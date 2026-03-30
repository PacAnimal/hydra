using Hydra.Keyboard;

namespace Hydra.Platform.MacOs;

// maps macOS virtual key codes to platform-independent KeyId constants.
// only covers non-character keys (function keys, arrows, modifiers, keypad).
// character keys (letters, numbers, punctuation) are resolved via UCKeyTranslate.
internal static class MacSpecialKeyMap
{
    private static readonly Dictionary<int, uint> Map = new()
    {
        // cursor / navigation
        { MacVirtualKey.LeftArrow, KeyId.Left },
        { MacVirtualKey.RightArrow, KeyId.Right },
        { MacVirtualKey.UpArrow, KeyId.Up },
        { MacVirtualKey.DownArrow, KeyId.Down },
        { MacVirtualKey.Home, KeyId.Home },
        { MacVirtualKey.End, KeyId.End },
        { MacVirtualKey.PageUp, KeyId.PageUp },
        { MacVirtualKey.PageDown, KeyId.PageDown },
        { MacVirtualKey.Help, KeyId.Insert },   // Apple keyboards have Help where Insert is

        // function keys
        { MacVirtualKey.F1, KeyId.F1 },
        { MacVirtualKey.F2, KeyId.F2 },
        { MacVirtualKey.F3, KeyId.F3 },
        { MacVirtualKey.F4, KeyId.F4 },
        { MacVirtualKey.F5, KeyId.F5 },
        { MacVirtualKey.F6, KeyId.F6 },
        { MacVirtualKey.F7, KeyId.F7 },
        { MacVirtualKey.F8, KeyId.F8 },
        { MacVirtualKey.F9, KeyId.F9 },
        { MacVirtualKey.F10, KeyId.F10 },
        { MacVirtualKey.F11, KeyId.F11 },
        { MacVirtualKey.F12, KeyId.F12 },
        { MacVirtualKey.F13, KeyId.F13 },
        { MacVirtualKey.F14, KeyId.F14 },
        { MacVirtualKey.F15, KeyId.F15 },
        { MacVirtualKey.F16, KeyId.F16 },

        // keypad
        { MacVirtualKey.Keypad0, KeyId.KP_0 },
        { MacVirtualKey.Keypad1, KeyId.KP_1 },
        { MacVirtualKey.Keypad2, KeyId.KP_2 },
        { MacVirtualKey.Keypad3, KeyId.KP_3 },
        { MacVirtualKey.Keypad4, KeyId.KP_4 },
        { MacVirtualKey.Keypad5, KeyId.KP_5 },
        { MacVirtualKey.Keypad6, KeyId.KP_6 },
        { MacVirtualKey.Keypad7, KeyId.KP_7 },
        { MacVirtualKey.Keypad8, KeyId.KP_8 },
        { MacVirtualKey.Keypad9, KeyId.KP_9 },
        { MacVirtualKey.KeypadDecimal, KeyId.KP_Decimal },
        { MacVirtualKey.KeypadEquals, KeyId.KP_Equal },
        { MacVirtualKey.KeypadMultiply, KeyId.KP_Multiply },
        { MacVirtualKey.KeypadPlus, KeyId.KP_Add },
        { MacVirtualKey.KeypadDivide, KeyId.KP_Divide },
        { MacVirtualKey.KeypadMinus, KeyId.KP_Subtract },
        { MacVirtualKey.KeypadEnter, KeyId.KP_Enter },
        { MacVirtualKey.KeypadClear, KeyId.NumLock },

        // modifiers — left and right map to distinct KeyIds
        { MacVirtualKey.Shift, KeyId.Shift_L },
        { MacVirtualKey.RightShift, KeyId.Shift_R },
        { MacVirtualKey.Control, KeyId.Control_L },
        { MacVirtualKey.RightControl, KeyId.Control_R },
        { MacVirtualKey.Option, KeyId.Alt_L },
        { MacVirtualKey.RightOption, KeyId.Alt_R },
        { MacVirtualKey.Command, KeyId.Super_L },
        { MacVirtualKey.RightCommand, KeyId.Super_R },
        { MacVirtualKey.CapsLock, KeyId.CapsLock },
    };

    internal static bool TryGet(int vkCode, out uint keyId) => Map.TryGetValue(vkCode, out keyId);

    internal static IReadOnlyDictionary<int, uint> All => Map;
}
