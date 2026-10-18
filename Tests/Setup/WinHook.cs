using Hydra.Keyboard;
using Hydra.Platform.Windows;

namespace Tests.Setup;

// low-level keyboard hook events, as the hook thread hands them over
internal static class WinHook
{
    public static KBDLLHOOKSTRUCT Hook(uint vk, uint flags = 0, uint time = 1) => new() { vkCode = vk, flags = flags, time = time };

    public static KeyEvent[]? Down(WinKeyResolver r, uint vk, uint flags = 0, uint time = 1) =>
        r.Resolve(NativeMethods.WM_KEYDOWN, Hook(vk, flags, time));

    public static KeyEvent[]? Up(WinKeyResolver r, uint vk, uint flags = 0, uint time = 1) =>
        r.Resolve(NativeMethods.WM_KEYUP, Hook(vk, flags, time));
}
