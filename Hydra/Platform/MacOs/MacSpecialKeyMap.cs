using Hydra.Keyboard;

namespace Hydra.Platform.MacOs;

// maps macOS virtual key codes to SpecialKey constants.
// only covers non-character keys (function keys, arrows, modifiers, keypad).
// character keys (letters, numbers, punctuation) are resolved via UCKeyTranslate.
internal static class MacSpecialKeyMap
{
    private static readonly Dictionary<int, SpecialKey> Map = new()
    {
        // cursor / navigation
        { MacVirtualKey.LeftArrow, SpecialKey.Left },
        { MacVirtualKey.RightArrow, SpecialKey.Right },
        { MacVirtualKey.UpArrow, SpecialKey.Up },
        { MacVirtualKey.DownArrow, SpecialKey.Down },
        { MacVirtualKey.Home, SpecialKey.Home },
        { MacVirtualKey.End, SpecialKey.End },
        { MacVirtualKey.PageUp, SpecialKey.PageUp },
        { MacVirtualKey.PageDown, SpecialKey.PageDown },
        { MacVirtualKey.Help, SpecialKey.Insert },   // Apple keyboards have Help where Insert is

        // function keys
        { MacVirtualKey.F1, SpecialKey.F1 },
        { MacVirtualKey.F2, SpecialKey.F2 },
        { MacVirtualKey.F3, SpecialKey.F3 },
        { MacVirtualKey.F4, SpecialKey.F4 },
        { MacVirtualKey.F5, SpecialKey.F5 },
        { MacVirtualKey.F6, SpecialKey.F6 },
        { MacVirtualKey.F7, SpecialKey.F7 },
        { MacVirtualKey.F8, SpecialKey.F8 },
        { MacVirtualKey.F9, SpecialKey.F9 },
        { MacVirtualKey.F10, SpecialKey.F10 },
        { MacVirtualKey.F11, SpecialKey.F11 },
        { MacVirtualKey.F12, SpecialKey.F12 },
        { MacVirtualKey.F13, SpecialKey.F13 },
        { MacVirtualKey.F14, SpecialKey.F14 },
        { MacVirtualKey.F15, SpecialKey.F15 },
        { MacVirtualKey.F16, SpecialKey.F16 },

        // keypad
        { MacVirtualKey.Keypad0, SpecialKey.KP_0 },
        { MacVirtualKey.Keypad1, SpecialKey.KP_1 },
        { MacVirtualKey.Keypad2, SpecialKey.KP_2 },
        { MacVirtualKey.Keypad3, SpecialKey.KP_3 },
        { MacVirtualKey.Keypad4, SpecialKey.KP_4 },
        { MacVirtualKey.Keypad5, SpecialKey.KP_5 },
        { MacVirtualKey.Keypad6, SpecialKey.KP_6 },
        { MacVirtualKey.Keypad7, SpecialKey.KP_7 },
        { MacVirtualKey.Keypad8, SpecialKey.KP_8 },
        { MacVirtualKey.Keypad9, SpecialKey.KP_9 },
        { MacVirtualKey.KeypadDecimal, SpecialKey.KP_Decimal },
        { MacVirtualKey.KeypadEquals, SpecialKey.KP_Equal },
        { MacVirtualKey.KeypadMultiply, SpecialKey.KP_Multiply },
        { MacVirtualKey.KeypadPlus, SpecialKey.KP_Add },
        { MacVirtualKey.KeypadDivide, SpecialKey.KP_Divide },
        { MacVirtualKey.KeypadMinus, SpecialKey.KP_Subtract },
        { MacVirtualKey.KeypadEnter, SpecialKey.KP_Enter },
        { MacVirtualKey.KeypadClear, SpecialKey.NumLock },

        // modifiers — left and right map to distinct SpecialKeys
        { MacVirtualKey.Shift, SpecialKey.Shift_L },
        { MacVirtualKey.RightShift, SpecialKey.Shift_R },
        { MacVirtualKey.Control, SpecialKey.Control_L },
        { MacVirtualKey.RightControl, SpecialKey.Control_R },
        { MacVirtualKey.Option, SpecialKey.Alt_L },
        { MacVirtualKey.RightOption, SpecialKey.Alt_R },
        { MacVirtualKey.Command, SpecialKey.Super_L },
        { MacVirtualKey.RightCommand, SpecialKey.Super_R },
        { MacVirtualKey.CapsLock, SpecialKey.CapsLock },
    };

    internal static bool TryGet(int vkCode, out SpecialKey key) => Map.TryGetValue(vkCode, out key);

    internal static IReadOnlyDictionary<int, SpecialKey> All => Map;
}
