using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Windows;

// builds SendInput events and sends them as one batch, which nothing else's input can land inside
[SupportedOSPlatform("windows")]
internal static class InputSender
{
    internal static INPUT KeyInput(int vk, uint flags = 0) =>
        new() { type = NativeMethods.INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = (ushort)vk, dwFlags = flags } };

    internal static INPUT MouseInput(uint flags, int dx = 0, int dy = 0, uint mouseData = 0) =>
        new() { type = NativeMethods.INPUT_MOUSE, mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = flags, mouseData = mouseData } };

    // returns how many events Windows took, 0 on failure
    internal static unsafe uint Send(params ReadOnlySpan<INPUT> inputs)
    {
        fixed (INPUT* p = inputs)
            return NativeMethods.SendInput((uint)inputs.Length, p, sizeof(INPUT));
    }

    internal static uint Key(int vk, uint flags = 0) => Send(KeyInput(vk, flags));

    internal static uint VkTap(int vk, uint flags = 0) =>
        Send(KeyInput(vk, flags), KeyInput(vk, flags | NativeMethods.KEYEVENTF_KEYUP));

    // every KEYEVENTF_UNICODE event is VK_PACKET, so a down overlapping another char's makes Windows retype the first
    internal static uint UnicodeTap(char ch) =>
        Send(UnicodeInput(ch, 0), UnicodeInput(ch, NativeMethods.KEYEVENTF_KEYUP));

    internal static void WarnIfFailed(this uint sent, ILogger log, string what)
    {
        if (sent == 0) log.LogWarning("SendInput({What}) failed (error {Error})", what, Marshal.GetLastWin32Error());
    }

    private static INPUT UnicodeInput(char ch, uint flags) =>
        new() { type = NativeMethods.INPUT_KEYBOARD, ki = new KEYBDINPUT { wScan = ch, dwFlags = NativeMethods.KEYEVENTF_UNICODE | flags } };
}
