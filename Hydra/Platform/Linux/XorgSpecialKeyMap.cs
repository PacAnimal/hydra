using Hydra.Keyboard;

namespace Hydra.Platform.Linux;

// maps X11 keysyms to platform-independent KeyId constants.
// only covers special (non-character) keys; printable keys are handled mechanically
// in XorgKeyResolver (MISCELLANY range: keysym - 0x1000 = KeyId).
internal static class XorgSpecialKeyMap
{
    private static readonly Dictionary<ulong, uint> Map = new()
    {
        // tty
        { XorgVirtualKey.BackSpace, KeyId.BackSpace },
        { XorgVirtualKey.Tab, KeyId.Tab },
        { XorgVirtualKey.Return, KeyId.Return },
        { XorgVirtualKey.Escape, KeyId.Escape },
        { XorgVirtualKey.Delete, KeyId.Delete },

        // cursor / navigation
        { XorgVirtualKey.Home, KeyId.Home },
        { XorgVirtualKey.Left, KeyId.Left },
        { XorgVirtualKey.Up, KeyId.Up },
        { XorgVirtualKey.Right, KeyId.Right },
        { XorgVirtualKey.Down, KeyId.Down },
        { XorgVirtualKey.PageUp, KeyId.PageUp },
        { XorgVirtualKey.PageDown, KeyId.PageDown },
        { XorgVirtualKey.End, KeyId.End },
        { XorgVirtualKey.Insert, KeyId.Insert },

        // misc
        { XorgVirtualKey.NumLock, KeyId.NumLock },
        { XorgVirtualKey.ScrollLock, KeyId.ScrollLock },

        // keypad
        { XorgVirtualKey.KP_Space, KeyId.KP_Space },
        { XorgVirtualKey.KP_Tab, KeyId.KP_Tab },
        { XorgVirtualKey.KP_Enter, KeyId.KP_Enter },
        { XorgVirtualKey.KP_Equal, KeyId.KP_Equal },
        { XorgVirtualKey.KP_Multiply, KeyId.KP_Multiply },
        { XorgVirtualKey.KP_Add, KeyId.KP_Add },
        { XorgVirtualKey.KP_Subtract, KeyId.KP_Subtract },
        { XorgVirtualKey.KP_Decimal, KeyId.KP_Decimal },
        { XorgVirtualKey.KP_Divide, KeyId.KP_Divide },
        { XorgVirtualKey.KP_0, KeyId.KP_0 },
        { XorgVirtualKey.KP_1, KeyId.KP_1 },
        { XorgVirtualKey.KP_2, KeyId.KP_2 },
        { XorgVirtualKey.KP_3, KeyId.KP_3 },
        { XorgVirtualKey.KP_4, KeyId.KP_4 },
        { XorgVirtualKey.KP_5, KeyId.KP_5 },
        { XorgVirtualKey.KP_6, KeyId.KP_6 },
        { XorgVirtualKey.KP_7, KeyId.KP_7 },
        { XorgVirtualKey.KP_8, KeyId.KP_8 },
        { XorgVirtualKey.KP_9, KeyId.KP_9 },

        // function keys
        { XorgVirtualKey.F1, KeyId.F1 },
        { XorgVirtualKey.F2, KeyId.F2 },
        { XorgVirtualKey.F3, KeyId.F3 },
        { XorgVirtualKey.F4, KeyId.F4 },
        { XorgVirtualKey.F5, KeyId.F5 },
        { XorgVirtualKey.F6, KeyId.F6 },
        { XorgVirtualKey.F7, KeyId.F7 },
        { XorgVirtualKey.F8, KeyId.F8 },
        { XorgVirtualKey.F9, KeyId.F9 },
        { XorgVirtualKey.F10, KeyId.F10 },
        { XorgVirtualKey.F11, KeyId.F11 },
        { XorgVirtualKey.F12, KeyId.F12 },
        { XorgVirtualKey.F13, KeyId.F13 },
        { XorgVirtualKey.F14, KeyId.F14 },
        { XorgVirtualKey.F15, KeyId.F15 },
        { XorgVirtualKey.F16, KeyId.F16 },

        // modifiers
        { XorgVirtualKey.Shift_L, KeyId.Shift_L },
        { XorgVirtualKey.Shift_R, KeyId.Shift_R },
        { XorgVirtualKey.Control_L, KeyId.Control_L },
        { XorgVirtualKey.Control_R, KeyId.Control_R },
        { XorgVirtualKey.CapsLock, KeyId.CapsLock },
        { XorgVirtualKey.Alt_L, KeyId.Alt_L },
        { XorgVirtualKey.Alt_R, KeyId.Alt_R },
        { XorgVirtualKey.Super_L, KeyId.Super_L },
        { XorgVirtualKey.Super_R, KeyId.Super_R },
    };

    internal static bool TryGet(ulong keysym, out uint keyId) => Map.TryGetValue(keysym, out keyId);
}
