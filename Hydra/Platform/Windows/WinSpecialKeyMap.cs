using Hydra.Keyboard;

namespace Hydra.Platform.Windows;

// maps Windows virtual key codes to SpecialKey constants.
// only covers non-character keys (function keys, arrows, modifiers, keypad).
// character keys are resolved via ToUnicodeEx.
internal static class WinSpecialKeyMap
{
    private static readonly Dictionary<int, SpecialKey> Map = new()
    {
        // tty
        { WinVirtualKey.Back, SpecialKey.BackSpace },
        { WinVirtualKey.Tab, SpecialKey.Tab },
        { WinVirtualKey.Return, SpecialKey.Return },
        { WinVirtualKey.Escape, SpecialKey.Escape },

        // cursor / navigation
        { WinVirtualKey.Left, SpecialKey.Left },
        { WinVirtualKey.Right, SpecialKey.Right },
        { WinVirtualKey.Up, SpecialKey.Up },
        { WinVirtualKey.Down, SpecialKey.Down },
        { WinVirtualKey.Home, SpecialKey.Home },
        { WinVirtualKey.End, SpecialKey.End },
        { WinVirtualKey.Prior, SpecialKey.PageUp },
        { WinVirtualKey.Next, SpecialKey.PageDown },
        { WinVirtualKey.Insert, SpecialKey.Insert },
        { WinVirtualKey.Delete, SpecialKey.Delete },

        // function keys
        { WinVirtualKey.F1, SpecialKey.F1 },
        { WinVirtualKey.F2, SpecialKey.F2 },
        { WinVirtualKey.F3, SpecialKey.F3 },
        { WinVirtualKey.F4, SpecialKey.F4 },
        { WinVirtualKey.F5, SpecialKey.F5 },
        { WinVirtualKey.F6, SpecialKey.F6 },
        { WinVirtualKey.F7, SpecialKey.F7 },
        { WinVirtualKey.F8, SpecialKey.F8 },
        { WinVirtualKey.F9, SpecialKey.F9 },
        { WinVirtualKey.F10, SpecialKey.F10 },
        { WinVirtualKey.F11, SpecialKey.F11 },
        { WinVirtualKey.F12, SpecialKey.F12 },
        { WinVirtualKey.F13, SpecialKey.F13 },
        { WinVirtualKey.F14, SpecialKey.F14 },
        { WinVirtualKey.F15, SpecialKey.F15 },
        { WinVirtualKey.F16, SpecialKey.F16 },
        { WinVirtualKey.F17, SpecialKey.F17 },
        { WinVirtualKey.F18, SpecialKey.F18 },
        { WinVirtualKey.F19, SpecialKey.F19 },
        { WinVirtualKey.F20, SpecialKey.F20 },

        // keypad
        { WinVirtualKey.Numpad0, SpecialKey.KP_0 },
        { WinVirtualKey.Numpad1, SpecialKey.KP_1 },
        { WinVirtualKey.Numpad2, SpecialKey.KP_2 },
        { WinVirtualKey.Numpad3, SpecialKey.KP_3 },
        { WinVirtualKey.Numpad4, SpecialKey.KP_4 },
        { WinVirtualKey.Numpad5, SpecialKey.KP_5 },
        { WinVirtualKey.Numpad6, SpecialKey.KP_6 },
        { WinVirtualKey.Numpad7, SpecialKey.KP_7 },
        { WinVirtualKey.Numpad8, SpecialKey.KP_8 },
        { WinVirtualKey.Numpad9, SpecialKey.KP_9 },
        { WinVirtualKey.Decimal, SpecialKey.KP_Decimal },
        { WinVirtualKey.Multiply, SpecialKey.KP_Multiply },
        { WinVirtualKey.Add, SpecialKey.KP_Add },
        { WinVirtualKey.Subtract, SpecialKey.KP_Subtract },
        { WinVirtualKey.Divide, SpecialKey.KP_Divide },

        // modifiers — left and right map to distinct SpecialKeys
        { WinVirtualKey.LShift, SpecialKey.Shift_L },
        { WinVirtualKey.RShift, SpecialKey.Shift_R },
        { WinVirtualKey.LControl, SpecialKey.Control_L },
        { WinVirtualKey.RControl, SpecialKey.Control_R },
        { WinVirtualKey.LMenu, SpecialKey.Alt_L },
        { WinVirtualKey.RMenu, SpecialKey.Alt_R },
        { WinVirtualKey.LWin, SpecialKey.Super_L },
        { WinVirtualKey.RWin, SpecialKey.Super_R },
        { WinVirtualKey.Capital, SpecialKey.CapsLock },
        { WinVirtualKey.Numlock, SpecialKey.NumLock },
        { WinVirtualKey.Scroll, SpecialKey.ScrollLock },

        // media keys
        { WinVirtualKey.VolumeMute, SpecialKey.AudioMute },
        { WinVirtualKey.VolumeDown, SpecialKey.AudioVolumeDown },
        { WinVirtualKey.VolumeUp, SpecialKey.AudioVolumeUp },
        { WinVirtualKey.MediaNextTrack, SpecialKey.AudioNext },
        { WinVirtualKey.MediaPrevTrack, SpecialKey.AudioPrev },
        { WinVirtualKey.MediaStop, SpecialKey.AudioStop },
        { WinVirtualKey.MediaPlayPause, SpecialKey.AudioPlay },
    };

    internal static bool TryGet(int vkCode, out SpecialKey key) => Map.TryGetValue(vkCode, out key);

    internal static readonly Dictionary<SpecialKey, int> Reverse = Map.ToDictionary(kvp => kvp.Value, kvp => kvp.Key);
}
