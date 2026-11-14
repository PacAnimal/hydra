using Hydra.Platform.Linux;

namespace Tests.Desktops;

// the evdev codes the desktop tests press through UserHands
internal static class Keys
{
    public const int Key2 = 3;
    public const int KeyLeftCtrl = EvdevNativeMethods.KEY_LEFTCTRL;
    public const int KeyA = EvdevNativeMethods.KEY_A;
    public const int KeyL = 38;
    public const int KeyLeftShift = EvdevNativeMethods.KEY_LEFTSHIFT;
    public const int KeyM = 50;
    public const int KeyLeftAlt = EvdevNativeMethods.KEY_LEFTALT;
    public const int KeyLeftMeta = EvdevNativeMethods.KEY_LEFTMETA;
    public const int BtnLeft = EvdevNativeMethods.BTN_LEFT;

    // the default hotkeys, on ctrl+alt+super
    public static readonly int[] RelativeMouseHotkey = [KeyLeftCtrl, KeyLeftAlt, KeyLeftMeta, KeyM];
    public static readonly int[] CursorLockHotkey = [KeyLeftCtrl, KeyLeftAlt, KeyLeftMeta, KeyL];
}
