using Hydra.Platform.Linux;

namespace Tests.Desktops;

// the evdev codes the desktop tests press through UserHands
// a catalogue: a code is here before the test that presses it
// ReSharper disable UnusedMember.Global
internal static class Keys
{
    public const int Key2 = 3;
    public const int KeyLeftCtrl = EvdevNativeMethods.KEY_LEFTCTRL;
    public const int KeyA = EvdevNativeMethods.KEY_A;
    public const int KeyL = 38;
    public const int KeyLeftShift = EvdevNativeMethods.KEY_LEFTSHIFT;
    public const int KeyM = 50;
    public const int KeyC = 46;
    public const int KeyV = 47;
    public const int KeyG = 34;
    public const int KeyH = 35;
    public const int KeyB = 48;
    public const int KeyD = 32;
    public const int KeyT = 20;
    public const int KeyN = 49;
    public const int KeyRightBrace = 27;
    public const int KeyS = 31;
    public const int KeySemicolon = 39;
    public const int KeyApostrophe = 40;
    public const int KeyBackslash = 43;
    public const int KeySlash = 53;
    public const int KeyLeftAlt = EvdevNativeMethods.KEY_LEFTALT;
    public const int KeyLeftMeta = EvdevNativeMethods.KEY_LEFTMETA;
    public const int BtnLeft = EvdevNativeMethods.BTN_LEFT;
    public const int KeyRight = 106;

    // the default hotkeys, on ctrl+alt+super
    public static readonly int[] RelativeMouseHotkey = [KeyLeftCtrl, KeyLeftAlt, KeyLeftMeta, KeyM];
    public static readonly int[] CursorLockHotkey = [KeyLeftCtrl, KeyLeftAlt, KeyLeftMeta, KeyL];
    public static readonly int[] SwitchRightHotkey = [KeyLeftCtrl, KeyLeftAlt, KeyLeftMeta, KeyRight];
}
