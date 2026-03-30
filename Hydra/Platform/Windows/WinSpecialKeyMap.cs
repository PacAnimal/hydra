using Hydra.Keyboard;

namespace Hydra.Platform.Windows;

// maps Windows virtual key codes to platform-independent KeyId constants.
// only covers non-character keys (function keys, arrows, modifiers, keypad).
// character keys are resolved via ToUnicodeEx.
internal static class WinSpecialKeyMap
{
    private static readonly Dictionary<int, uint> Map = new()
    {
        // cursor / navigation
        { WinVirtualKey.Left, KeyId.Left },
        { WinVirtualKey.Right, KeyId.Right },
        { WinVirtualKey.Up, KeyId.Up },
        { WinVirtualKey.Down, KeyId.Down },
        { WinVirtualKey.Home, KeyId.Home },
        { WinVirtualKey.End, KeyId.End },
        { WinVirtualKey.Prior, KeyId.PageUp },
        { WinVirtualKey.Next, KeyId.PageDown },
        { WinVirtualKey.Insert, KeyId.Insert },
        { WinVirtualKey.Delete, KeyId.Delete },

        // function keys
        { WinVirtualKey.F1, KeyId.F1 },
        { WinVirtualKey.F2, KeyId.F2 },
        { WinVirtualKey.F3, KeyId.F3 },
        { WinVirtualKey.F4, KeyId.F4 },
        { WinVirtualKey.F5, KeyId.F5 },
        { WinVirtualKey.F6, KeyId.F6 },
        { WinVirtualKey.F7, KeyId.F7 },
        { WinVirtualKey.F8, KeyId.F8 },
        { WinVirtualKey.F9, KeyId.F9 },
        { WinVirtualKey.F10, KeyId.F10 },
        { WinVirtualKey.F11, KeyId.F11 },
        { WinVirtualKey.F12, KeyId.F12 },
        { WinVirtualKey.F13, KeyId.F13 },
        { WinVirtualKey.F14, KeyId.F14 },
        { WinVirtualKey.F15, KeyId.F15 },
        { WinVirtualKey.F16, KeyId.F16 },

        // keypad
        { WinVirtualKey.Numpad0, KeyId.KP_0 },
        { WinVirtualKey.Numpad1, KeyId.KP_1 },
        { WinVirtualKey.Numpad2, KeyId.KP_2 },
        { WinVirtualKey.Numpad3, KeyId.KP_3 },
        { WinVirtualKey.Numpad4, KeyId.KP_4 },
        { WinVirtualKey.Numpad5, KeyId.KP_5 },
        { WinVirtualKey.Numpad6, KeyId.KP_6 },
        { WinVirtualKey.Numpad7, KeyId.KP_7 },
        { WinVirtualKey.Numpad8, KeyId.KP_8 },
        { WinVirtualKey.Numpad9, KeyId.KP_9 },
        { WinVirtualKey.Decimal, KeyId.KP_Decimal },
        { WinVirtualKey.Multiply, KeyId.KP_Multiply },
        { WinVirtualKey.Add, KeyId.KP_Add },
        { WinVirtualKey.Subtract, KeyId.KP_Subtract },
        { WinVirtualKey.Divide, KeyId.KP_Divide },

        // modifiers — left and right map to distinct KeyIds
        { WinVirtualKey.LShift, KeyId.Shift_L },
        { WinVirtualKey.RShift, KeyId.Shift_R },
        { WinVirtualKey.LControl, KeyId.Control_L },
        { WinVirtualKey.RControl, KeyId.Control_R },
        { WinVirtualKey.LMenu, KeyId.Alt_L },
        { WinVirtualKey.RMenu, KeyId.Alt_R },
        { WinVirtualKey.LWin, KeyId.Super_L },
        { WinVirtualKey.RWin, KeyId.Super_R },
        { WinVirtualKey.Capital, KeyId.CapsLock },
        { WinVirtualKey.Numlock, KeyId.NumLock },
        { WinVirtualKey.Scroll, KeyId.ScrollLock },
    };

    internal static bool TryGet(int vkCode, out uint keyId) => Map.TryGetValue(vkCode, out keyId);

    internal static IReadOnlyDictionary<int, uint> All => Map;
}
