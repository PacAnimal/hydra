using Hydra.Config;
using Hydra.Keyboard;

namespace Hydra.Platform.Linux;

// The hotkeys an Xorg master must hear while the cursor is home, where nothing but a passive grab hands it a key: the
// cursor lock's and the monitor switch's, as configured.
internal static class XorgHotkeyGrab
{
    private static readonly HotkeyAction[] Actions =
        [HotkeyAction.ToggleCursorLock, HotkeyAction.SwitchMonitorUp, HotkeyAction.SwitchMonitorDown, HotkeyAction.SwitchMonitorLeft, HotkeyAction.SwitchMonitorRight];

    // each bound chord as a keycode and modifier mask; one whose key this keyboard lacks is left out
    internal static List<GrabbedKey> Keys(nint display, HotkeyBindings hotkeys)
    {
        var keys = new List<GrabbedKey>();
        foreach (var action in Actions)
            foreach (var binding in hotkeys.For(action))
            {
                var keysym = binding.Key is { } key ? XorgKeysym.Of(key) : binding.Character is { } ch ? XorgKeysym.Of(ch) : 0;
                var keycode = keysym == 0 ? 0 : (int)NativeMethods.XKeysymToKeycode(display, keysym);
                if (keycode != 0) keys.Add(new GrabbedKey(keycode, ModMask(binding.Modifiers), action));
            }
        return keys;
    }

    private static uint ModMask(KeyModifiers mods) =>
        ((mods & KeyModifiers.Shift) != 0 ? NativeMethods.ShiftMask : 0)
        | ((mods & KeyModifiers.Control) != 0 ? NativeMethods.ControlMask : 0)
        | ((mods & KeyModifiers.Alt) != 0 ? NativeMethods.Mod1Mask : 0)
        | ((mods & KeyModifiers.Super) != 0 ? NativeMethods.Mod4Mask : 0)
        | ((mods & KeyModifiers.AltGr) != 0 ? NativeMethods.Mod5Mask : 0);
}

internal readonly record struct GrabbedKey(int Keycode, uint Mods, HotkeyAction Action);
