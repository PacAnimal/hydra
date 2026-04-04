using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Relay;
using Hydra.Screen;

namespace Hydra.Platform.Windows;

public sealed class WindowsOutputHandler : IPlatformOutput
{
    // vk codes that require KEYEVENTF_EXTENDEDKEY (right-side modifiers, nav cluster, arrows)
    private static readonly HashSet<ulong> ExtendedKeys =
    [
        WinVirtualKey.RControl, WinVirtualKey.RMenu,
        WinVirtualKey.Insert, WinVirtualKey.Delete,
        WinVirtualKey.Home, WinVirtualKey.End,
        WinVirtualKey.Prior, WinVirtualKey.Next,
        WinVirtualKey.Left, WinVirtualKey.Up, WinVirtualKey.Right, WinVirtualKey.Down,
        WinVirtualKey.LWin, WinVirtualKey.RWin,
        WinVirtualKey.Divide,   // numpad /
    ];

    public List<DetectedScreen> GetAllScreens() => WindowsDisplayHelper.GetAllScreens();

    public unsafe void MoveMouse(int x, int y)
    {
        var vLeft = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        var vTop = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        var vWidth = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        var vHeight = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        if (vWidth == 0) vWidth = 1;
        if (vHeight == 0) vHeight = 1;

        // normalize to 0-65535 across entire virtual desktop (all monitors)
        var dx = ((x - vLeft) * 65536 + vWidth / 2) / vWidth;
        var dy = ((y - vTop) * 65536 + vHeight / 2) / vHeight;

        var input = new INPUT
        {
            type = NativeMethods.INPUT_MOUSE,
            mi = new MOUSEINPUT
            {
                dx = dx,
                dy = dy,
                dwFlags = NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_VIRTUALDESK,
            },
        };
        _ = NativeMethods.SendInput(1, &input, sizeof(INPUT));
    }

    public unsafe void InjectKey(KeyEventMessage msg)
    {
        var isUp = msg.Type == KeyEventType.KeyUp;

        if (msg.Character is { } ch)
        {
            // inject via unicode scan code — no vk mapping needed
            var flags = NativeMethods.KEYEVENTF_UNICODE | (isUp ? NativeMethods.KEYEVENTF_KEYUP : 0);
            var input = new INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                ki = new KEYBDINPUT { wVk = 0, wScan = (ushort)ch, dwFlags = flags },
            };
            _ = NativeMethods.SendInput(1, &input, sizeof(INPUT));
        }
        else if (msg.Key is { } key && WinSpecialKeyMap.Instance.Reverse.TryGetValue(key, out var vk))
        {
            var flags = isUp ? NativeMethods.KEYEVENTF_KEYUP : 0u;
            if (ExtendedKeys.Contains(vk))
                flags |= NativeMethods.KEYEVENTF_EXTENDEDKEY;

            var input = new INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                ki = new KEYBDINPUT { wVk = (ushort)vk, dwFlags = flags },
            };
            _ = NativeMethods.SendInput(1, &input, sizeof(INPUT));
        }
    }

    public unsafe void InjectMouseButton(MouseButtonMessage msg)
    {
        var (downFlag, upFlag, mouseData) = msg.Button switch
        {
            MouseButton.Left => (NativeMethods.MOUSEEVENTF_LEFTDOWN, NativeMethods.MOUSEEVENTF_LEFTUP, 0u),
            MouseButton.Right => (NativeMethods.MOUSEEVENTF_RIGHTDOWN, NativeMethods.MOUSEEVENTF_RIGHTUP, 0u),
            MouseButton.Middle => (NativeMethods.MOUSEEVENTF_MIDDLEDOWN, NativeMethods.MOUSEEVENTF_MIDDLEUP, 0u),
            MouseButton.Extra1 => (NativeMethods.MOUSEEVENTF_XDOWN, NativeMethods.MOUSEEVENTF_XUP, (uint)NativeMethods.XBUTTON1),
            MouseButton.Extra2 => (NativeMethods.MOUSEEVENTF_XDOWN, NativeMethods.MOUSEEVENTF_XUP, (uint)NativeMethods.XBUTTON2),
            _ => (0u, 0u, 0u),
        };
        if (downFlag == 0) return;

        var input = new INPUT
        {
            type = NativeMethods.INPUT_MOUSE,
            mi = new MOUSEINPUT
            {
                dwFlags = msg.IsPressed ? downFlag : upFlag,
                mouseData = mouseData,
            },
        };
        _ = NativeMethods.SendInput(1, &input, sizeof(INPUT));
    }

    public unsafe void InjectMouseScroll(MouseScrollMessage msg)
    {
        if (msg.YDelta != 0)
        {
            var input = new INPUT
            {
                type = NativeMethods.INPUT_MOUSE,
                mi = new MOUSEINPUT { dwFlags = NativeMethods.MOUSEEVENTF_WHEEL, mouseData = (uint)(short)msg.YDelta },
            };
            _ = NativeMethods.SendInput(1, &input, sizeof(INPUT));
        }

        if (msg.XDelta != 0)
        {
            var input = new INPUT
            {
                type = NativeMethods.INPUT_MOUSE,
                mi = new MOUSEINPUT { dwFlags = NativeMethods.MOUSEEVENTF_HWHEEL, mouseData = (uint)(short)msg.XDelta },
            };
            _ = NativeMethods.SendInput(1, &input, sizeof(INPUT));
        }
    }

    public void Dispose() { }
}
