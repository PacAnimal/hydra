using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Relay;

namespace Hydra.Platform.Windows;

public sealed class WindowsOutputHandler : IPlatformOutput, ICursorVisibility
{
    private bool _cursorHidden;
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

    public unsafe void MoveMouseRelative(int dx, int dy)
    {
        // disable mouse acceleration for 1:1 movement, then restore (matches input-leap approach)
        int* oldMouse = stackalloc int[3];
        int oldSpeed = 0;
        var saved = NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETMOUSE, 0, (nint)oldMouse, 0)
                 && NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETMOUSESPEED, 0, (nint)(&oldSpeed), 0);

        if (saved)
        {
            int* flat = stackalloc int[3];
            flat[0] = 0; flat[1] = 0; flat[2] = 0;
            int flatSpeed = 1;
            NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETMOUSE, 0, (nint)flat, 0);
            // SPI_SETMOUSESPEED takes the value directly as pvParam (not a pointer)
            NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETMOUSESPEED, 0, flatSpeed, 0);
        }

        var input = new INPUT
        {
            type = NativeMethods.INPUT_MOUSE,
            mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = NativeMethods.MOUSEEVENTF_MOVE },
        };
        _ = NativeMethods.SendInput(1, &input, sizeof(INPUT));

        if (saved)
        {
            NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETMOUSE, 0, (nint)oldMouse, 0);
            NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETMOUSESPEED, 0, oldSpeed, 0);
        }
    }

    public unsafe void InjectKey(KeyEventMessage msg)
    {
        var isUp = msg.Type == KeyEventType.KeyUp;

        if (msg.Character is { } ch)
        {
            // when shortcut modifiers are held, inject via vk so WM_KEYDOWN fires and apps see the shortcut.
            // KEYEVENTF_UNICODE generates WM_CHAR which bypasses shortcut detection entirely.
            // AltGr characters are excluded: the char is already resolved by the Mac side, inject it directly.
            const KeyModifiers shortcutMods = KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super;
            var scan = NativeMethods.VkKeyScanW(ch); // char implicit-converts to ushort
            var isAltGr = (msg.Modifiers & KeyModifiers.AltGr) != 0;
            var isSuper = (msg.Modifiers & KeyModifiers.Super) != 0;
            if (!isAltGr && (msg.Modifiers & shortcutMods) != 0 && scan != -1)
            {
                var vk = (ushort)(scan & 0xFF);
                if (isSuper && vk == 0x4C) // Win+L: UIPI blocks SendInput; use the API directly
                {
                    if (!isUp) NativeMethods.LockWorkStation();
                    return;
                }

                if (isSuper && !isUp)
                {
                    // batch Win down + key down in one SendInput so the shell sees them atomically
                    var inputs = stackalloc INPUT[2];
                    inputs[0] = new INPUT { type = NativeMethods.INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = (ushort)WinVirtualKey.LWin, dwFlags = NativeMethods.KEYEVENTF_EXTENDEDKEY } };
                    inputs[1] = new INPUT { type = NativeMethods.INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = vk } };
                    _ = NativeMethods.SendInput(2, inputs, sizeof(INPUT));
                }
                else
                {
                    var flags = isUp ? NativeMethods.KEYEVENTF_KEYUP : 0u;
                    var input = new INPUT
                    {
                        type = NativeMethods.INPUT_KEYBOARD,
                        ki = new KEYBDINPUT { wVk = vk, dwFlags = flags },
                    };
                    _ = NativeMethods.SendInput(1, &input, sizeof(INPUT));
                }
            }
            else
            {
                // normal typing — unicode scan code, no vk mapping needed
                var flags = NativeMethods.KEYEVENTF_UNICODE | (isUp ? NativeMethods.KEYEVENTF_KEYUP : 0);
                var input = new INPUT
                {
                    type = NativeMethods.INPUT_KEYBOARD,
                    ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = flags },
                };
                _ = NativeMethods.SendInput(1, &input, sizeof(INPUT));
            }
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
                mi = new MOUSEINPUT { dwFlags = NativeMethods.MOUSEEVENTF_WHEEL, mouseData = (uint)msg.YDelta },
            };
            _ = NativeMethods.SendInput(1, &input, sizeof(INPUT));
        }

        if (msg.XDelta != 0)
        {
            var input = new INPUT
            {
                type = NativeMethods.INPUT_MOUSE,
                mi = new MOUSEINPUT { dwFlags = NativeMethods.MOUSEEVENTF_HWHEEL, mouseData = (uint)msg.XDelta },
            };
            _ = NativeMethods.SendInput(1, &input, sizeof(INPUT));
        }
    }

    public unsafe void HideCursor()
    {
        if (_cursorHidden) return;
        byte andMask = 0xFF;
        byte xorMask = 0x00;
        var blank = NativeMethods.CreateCursor(nint.Zero, 0, 0, 1, 1, &andMask, &xorMask);
        if (blank == nint.Zero) return;
        foreach (var id in NativeMethods.AllCursorIds)
        {
            var copy = NativeMethods.CopyCursor(blank);
            if (copy != nint.Zero)
                NativeMethods.SetSystemCursor(copy, id);
        }
        NativeMethods.DestroyCursor(blank);
        _cursorHidden = true;
    }

    public void ShowCursor()
    {
        if (!_cursorHidden) return;
        NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETCURSORS, 0, nint.Zero, 0);
        _cursorHidden = false;
    }

    public CursorPosition GetCursorPosition()
    {
        NativeMethods.GetCursorPos(out var pt);
        return new CursorPosition(pt.x, pt.y);
    }

    public void Dispose()
    {
        if (_cursorHidden)
            NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETCURSORS, 0, nint.Zero, 0);
    }
}
