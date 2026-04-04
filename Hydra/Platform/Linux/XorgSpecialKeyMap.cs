using Hydra.Keyboard;

namespace Hydra.Platform.Linux;

// maps X11 keysyms to SpecialKey constants.
// only covers special (non-character) keys; printable keys are handled mechanically
// in XorgKeyResolver (MISCELLANY range: keysym | 0x01000000 = SpecialKey value).
internal sealed class XorgSpecialKeyMap : SpecialKeyMap
{
    internal static readonly XorgSpecialKeyMap Instance = new();

    protected override Dictionary<ulong, SpecialKey> Map { get; } = new()
    {
        // tty
        { XorgVirtualKey.BackSpace, SpecialKey.BackSpace },
        { XorgVirtualKey.Tab, SpecialKey.Tab },
        { XorgVirtualKey.Return, SpecialKey.Return },
        { XorgVirtualKey.Escape, SpecialKey.Escape },
        { XorgVirtualKey.Delete, SpecialKey.Delete },

        // cursor / navigation
        { XorgVirtualKey.Home, SpecialKey.Home },
        { XorgVirtualKey.Left, SpecialKey.Left },
        { XorgVirtualKey.Up, SpecialKey.Up },
        { XorgVirtualKey.Right, SpecialKey.Right },
        { XorgVirtualKey.Down, SpecialKey.Down },
        { XorgVirtualKey.PageUp, SpecialKey.PageUp },
        { XorgVirtualKey.PageDown, SpecialKey.PageDown },
        { XorgVirtualKey.End, SpecialKey.End },
        { XorgVirtualKey.Insert, SpecialKey.Insert },

        // misc
        { XorgVirtualKey.NumLock, SpecialKey.NumLock },
        { XorgVirtualKey.ScrollLock, SpecialKey.ScrollLock },

        // keypad
        { XorgVirtualKey.KP_Space, SpecialKey.KP_Space },
        { XorgVirtualKey.KP_Tab, SpecialKey.KP_Tab },
        { XorgVirtualKey.KP_Enter, SpecialKey.KP_Enter },
        { XorgVirtualKey.KP_Equal, SpecialKey.KP_Equal },
        { XorgVirtualKey.KP_Multiply, SpecialKey.KP_Multiply },
        { XorgVirtualKey.KP_Add, SpecialKey.KP_Add },
        { XorgVirtualKey.KP_Subtract, SpecialKey.KP_Subtract },
        { XorgVirtualKey.KP_Decimal, SpecialKey.KP_Decimal },
        { XorgVirtualKey.KP_Divide, SpecialKey.KP_Divide },
        { XorgVirtualKey.KP_0, SpecialKey.KP_0 },
        { XorgVirtualKey.KP_1, SpecialKey.KP_1 },
        { XorgVirtualKey.KP_2, SpecialKey.KP_2 },
        { XorgVirtualKey.KP_3, SpecialKey.KP_3 },
        { XorgVirtualKey.KP_4, SpecialKey.KP_4 },
        { XorgVirtualKey.KP_5, SpecialKey.KP_5 },
        { XorgVirtualKey.KP_6, SpecialKey.KP_6 },
        { XorgVirtualKey.KP_7, SpecialKey.KP_7 },
        { XorgVirtualKey.KP_8, SpecialKey.KP_8 },
        { XorgVirtualKey.KP_9, SpecialKey.KP_9 },

        // function keys
        { XorgVirtualKey.F1, SpecialKey.F1 },
        { XorgVirtualKey.F2, SpecialKey.F2 },
        { XorgVirtualKey.F3, SpecialKey.F3 },
        { XorgVirtualKey.F4, SpecialKey.F4 },
        { XorgVirtualKey.F5, SpecialKey.F5 },
        { XorgVirtualKey.F6, SpecialKey.F6 },
        { XorgVirtualKey.F7, SpecialKey.F7 },
        { XorgVirtualKey.F8, SpecialKey.F8 },
        { XorgVirtualKey.F9, SpecialKey.F9 },
        { XorgVirtualKey.F10, SpecialKey.F10 },
        { XorgVirtualKey.F11, SpecialKey.F11 },
        { XorgVirtualKey.F12, SpecialKey.F12 },
        { XorgVirtualKey.F13, SpecialKey.F13 },
        { XorgVirtualKey.F14, SpecialKey.F14 },
        { XorgVirtualKey.F15, SpecialKey.F15 },
        { XorgVirtualKey.F16, SpecialKey.F16 },
        { XorgVirtualKey.F17, SpecialKey.F17 },
        { XorgVirtualKey.F18, SpecialKey.F18 },
        { XorgVirtualKey.F19, SpecialKey.F19 },
        { XorgVirtualKey.F20, SpecialKey.F20 },

        // media keys (XF86 vendor keysyms)
        { XorgVirtualKey.XF86AudioMute, SpecialKey.AudioMute },
        { XorgVirtualKey.XF86AudioLowerVolume, SpecialKey.AudioVolumeDown },
        { XorgVirtualKey.XF86AudioRaiseVolume, SpecialKey.AudioVolumeUp },
        { XorgVirtualKey.XF86AudioNext, SpecialKey.AudioNext },
        { XorgVirtualKey.XF86AudioPrev, SpecialKey.AudioPrev },
        { XorgVirtualKey.XF86AudioStop, SpecialKey.AudioStop },
        { XorgVirtualKey.XF86AudioPlay, SpecialKey.AudioPlay },
        { XorgVirtualKey.XF86MonBrightnessUp, SpecialKey.BrightnessUp },
        { XorgVirtualKey.XF86MonBrightnessDown, SpecialKey.BrightnessDown },
        { XorgVirtualKey.XF86Eject, SpecialKey.Eject },

        // modifiers
        { XorgVirtualKey.ISO_Level3_Shift, SpecialKey.AltGr },
        { XorgVirtualKey.Shift_L, SpecialKey.Shift_L },
        { XorgVirtualKey.Shift_R, SpecialKey.Shift_R },
        { XorgVirtualKey.Control_L, SpecialKey.Control_L },
        { XorgVirtualKey.Control_R, SpecialKey.Control_R },
        { XorgVirtualKey.CapsLock, SpecialKey.CapsLock },
        { XorgVirtualKey.Alt_L, SpecialKey.Alt_L },
        { XorgVirtualKey.Alt_R, SpecialKey.Alt_R },
        { XorgVirtualKey.Super_L, SpecialKey.Super_L },
        { XorgVirtualKey.Super_R, SpecialKey.Super_R },
    };
}
