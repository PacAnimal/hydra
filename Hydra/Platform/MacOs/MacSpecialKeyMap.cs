using Hydra.Keyboard;

namespace Hydra.Platform.MacOs;

// maps macOS virtual key codes to SpecialKey constants.
// only covers non-character keys (function keys, arrows, modifiers, keypad).
// character keys (letters, numbers, punctuation) are resolved via UCKeyTranslate.
internal sealed class MacSpecialKeyMap : SpecialKeyMap
{
    internal static readonly MacSpecialKeyMap Instance = new();

    protected override Dictionary<ulong, SpecialKey> Map { get; } = new()
    {
        // tty
        { MacVirtualKey.Delete, SpecialKey.BackSpace },
        { MacVirtualKey.ForwardDelete, SpecialKey.Delete },
        { MacVirtualKey.Return, SpecialKey.Return },
        { MacVirtualKey.Tab, SpecialKey.Tab },
        { MacVirtualKey.Escape, SpecialKey.Escape },

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
        { MacVirtualKey.F14, SpecialKey.ScrollLock },
        { MacVirtualKey.F15, SpecialKey.F15 },
        { MacVirtualKey.F16, SpecialKey.F16 },
        { MacVirtualKey.F17, SpecialKey.F17 },
        { MacVirtualKey.F18, SpecialKey.F18 },
        { MacVirtualKey.F19, SpecialKey.F19 },
        { MacVirtualKey.F20, SpecialKey.F20 },

        // keypad
        // note: Keypad0–9 are intentionally absent — MacKeyResolver emits them as char events ('0'–'9')
        // before reaching this map, so KP_0–KP_9 entries here would be unreachable dead code.
        { MacVirtualKey.KeypadDecimal, SpecialKey.KP_Decimal },
        { MacVirtualKey.KeypadEquals, SpecialKey.KP_Equal },
        { MacVirtualKey.KeypadMultiply, SpecialKey.KP_Multiply },
        { MacVirtualKey.KeypadPlus, SpecialKey.KP_Add },
        { MacVirtualKey.KeypadDivide, SpecialKey.KP_Divide },
        { MacVirtualKey.KeypadMinus, SpecialKey.KP_Subtract },
        { MacVirtualKey.KeypadEnter, SpecialKey.KP_Enter },
        { MacVirtualKey.KeypadClear, SpecialKey.NumLock },

        // media (volume keys arrive as regular kCGEventKeyDown; play/next/prev/brightness/eject via NX_SYSDEFINED)
        { MacVirtualKey.VolumeUp, SpecialKey.AudioVolumeUp },
        { MacVirtualKey.VolumeDown, SpecialKey.AudioVolumeDown },
        { MacVirtualKey.Mute, SpecialKey.AudioMute },

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

    internal IReadOnlyDictionary<ulong, SpecialKey> All => Map;

    // output-only overrides: keys that need a different VK (and optional extra flags) when synthesizing output.
    // MoveToBeginningOfLine/MoveToEndOfLine are sent by Win/Linux masters in place of Home/End.
    // on Mac, line start/end is Command+Left/Right, not the native Home/End keys (Fn+Left/Right = document nav).
    // ScrollLock: F14 is the Mac equivalent (no dedicated ScrollLock key on Apple keyboards).
    // KP_Tab/KP_Space: no distinct numpad VK on Mac — map to regular Tab/Space.
    internal static readonly IReadOnlyDictionary<SpecialKey, (ushort Vk, ulong ExtraFlags)> OutputOverrides =
        new Dictionary<SpecialKey, (ushort Vk, ulong ExtraFlags)>
        {
            { SpecialKey.MoveToBeginningOfLine, ((ushort)MacVirtualKey.LeftArrow,  NativeMethods.KCGEventFlagMaskCommand) },
            { SpecialKey.MoveToEndOfLine,       ((ushort)MacVirtualKey.RightArrow, NativeMethods.KCGEventFlagMaskCommand) },
            { SpecialKey.ScrollLock,            ((ushort)MacVirtualKey.F14, 0UL) },
            { SpecialKey.KP_Tab,                ((ushort)MacVirtualKey.Tab, 0UL) },
            { SpecialKey.KP_Space,              ((ushort)MacVirtualKey.Space, 0UL) },
        };
}
