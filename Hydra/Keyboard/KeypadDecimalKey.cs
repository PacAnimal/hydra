using Hydra.Platform.Linux;
using Hydra.Platform.MacOs;
using Hydra.Platform.Windows;

namespace Hydra.Keyboard;

// which physical key each master's resolver stamps as KeyEvent.IsKeypadDecimal: the keypad key whose NumLock-off form on
// that master is KP_Delete, so modifierNumLock can give it that form or its character. A keypad comma that NumLock leaves
// alone is not it (ABNT2's extra key, VK_SEPARATOR, the JIS comma, X11's KP_Decimal- or KP_Separator-based key): false
// would otherwise turn it into a KP_Delete the key can never produce. Evdev's comma is, as Hydra's own evdev NumLock-off
// already sends it as KP_Delete (EvdevKeyResolver.NormalizeKeypadKeysym). Pure, so tested on every machine.
internal static class KeypadDecimalKey
{
    private const uint EvdevKpDot = 83, EvdevKpComma = 121;

    internal static bool Evdev(uint evdevCode) => evdevCode is EvdevKpDot or EvdevKpComma;

    // the key whose base (group 0, level 0) keysym is KP_Delete; the dedicated Delete is its own keysym
    internal static bool Xorg(ulong baseKeysym) => baseKeysym == XorgVirtualKey.KP_Delete;

    // a press tracked as VK_DECIMAL; with NumLock off it tracks as Delete and the keypad bit, and is a key already
    internal static bool Windows(int tracking) => tracking == WinVirtualKey.Decimal;

    internal static bool MacOs(int keycode) => (ulong)keycode == MacVirtualKey.KeypadDecimal;
}
