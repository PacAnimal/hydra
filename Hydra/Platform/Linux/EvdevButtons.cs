using Hydra.Mouse;

namespace Hydra.Platform.Linux;

// mouse buttons and their evdev BTN_* codes, both ways: evdev and EIS report buttons by these codes and EIS injects them
internal static class EvdevButtons
{
    // a code past the five Hydra names (BTN_FORWARD, BTN_BACK, ...) is reported as Extra2
    internal static MouseButton Button(uint code) => code switch
    {
        EvdevNativeMethods.BTN_LEFT => MouseButton.Left,
        EvdevNativeMethods.BTN_RIGHT => MouseButton.Right,
        EvdevNativeMethods.BTN_MIDDLE => MouseButton.Middle,
        EvdevNativeMethods.BTN_SIDE => MouseButton.Extra1,
        _ => MouseButton.Extra2,
    };

    // null for a button Hydra has no code for
    internal static uint? Code(MouseButton button) => button switch
    {
        MouseButton.Left => EvdevNativeMethods.BTN_LEFT,
        MouseButton.Right => EvdevNativeMethods.BTN_RIGHT,
        MouseButton.Middle => EvdevNativeMethods.BTN_MIDDLE,
        MouseButton.Extra1 => EvdevNativeMethods.BTN_SIDE,
        MouseButton.Extra2 => EvdevNativeMethods.BTN_EXTRA,
        _ => null,
    };
}
