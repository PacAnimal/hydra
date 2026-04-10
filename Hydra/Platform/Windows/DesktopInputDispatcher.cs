using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Relay;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Windows;

/// <summary>Routes SendInput calls to a worker thread always attached to the current input desktop.</summary>
/// <remarks>
/// SendInput is desktop-scoped: calls from a thread attached to winsta0\Default are silently dropped
/// when the active input desktop is winsta0\Winlogon (lock screen) or the secure desktop (UAC prompts).
/// This class polls OpenInputDesktop every 200ms and re-attaches the worker thread via SetThreadDesktop
/// whenever the input desktop changes. Requires the process to run as SYSTEM (winlogon token) so that
/// OpenInputDesktop succeeds on restricted desktops.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class DesktopInputDispatcher : IDisposable
{
    private const uint DesktopAccess = NativeMethods.DESKTOP_READOBJECTS | NativeMethods.DESKTOP_WRITEOBJECTS;

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

    private readonly ILogger _log;
    private readonly BlockingCollection<InputCommand> _queue = [];
    private readonly Timer _pollTimer;
    private nint _activeDesktop;
    private string _activeDesktopName;
    private bool _disposed;

    internal DesktopInputDispatcher(ILogger log)
    {
        _log = log;
        _activeDesktop = NativeMethods.OpenInputDesktop(NativeMethods.DF_ALLOWOTHERACCOUNTHOOK, true, DesktopAccess);
        _activeDesktopName = GetDesktopName(_activeDesktop);
        if (_activeDesktop == nint.Zero)
            _log.LogWarning("OpenInputDesktop failed at startup (error {Error})", Marshal.GetLastWin32Error());
        else
            _log.LogInformation("Desktop input dispatcher started, current desktop: {Name}", _activeDesktopName);
        StartWorker(_activeDesktop);
        _pollTimer = new Timer(_ => PollDesktop(), null, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200));
    }

    internal void Dispatch(InputCommand cmd)
    {
        if (!_disposed)
            _queue.TryAdd(cmd);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer.Dispose();
        _queue.CompleteAdding();
        if (_activeDesktop != nint.Zero)
        {
            NativeMethods.CloseDesktop(_activeDesktop);
            _activeDesktop = nint.Zero;
        }
    }

    private void StartWorker(nint hDesk)
    {
        var t = new Thread(() =>
        {
            if (hDesk != nint.Zero)
            {
                if (!NativeMethods.SetThreadDesktop(hDesk))
                    _log.LogWarning("SetThreadDesktop failed at worker startup (error {Error})", Marshal.GetLastWin32Error());
            }
            foreach (var cmd in _queue.GetConsumingEnumerable())
                Execute(cmd);
        })
        {
            IsBackground = true,
            Name = "HydraDesktopInput",
        };
        t.Start();
    }

    private void PollDesktop()
    {
        if (_disposed) return;

        var hDesk = NativeMethods.OpenInputDesktop(NativeMethods.DF_ALLOWOTHERACCOUNTHOOK, true, DesktopAccess);
        if (hDesk == nint.Zero)
        {
            _log.LogWarning("OpenInputDesktop failed during poll (error {Error})", Marshal.GetLastWin32Error());
            return;
        }

        var name = GetDesktopName(hDesk);
        if (name == _activeDesktopName)
        {
            NativeMethods.CloseDesktop(hDesk);
            return;
        }

        _log.LogInformation("Input desktop changed: {Old} → {New}", _activeDesktopName, name);

        var oldDesk = _activeDesktop;
        _activeDesktop = hDesk;
        _activeDesktopName = name;

        // re-attach the worker thread to the new desktop; close old handle after the thread detaches
        _queue.TryAdd(new SwitchDesktopCommand(hDesk, oldDesk, name));
    }

    private void Execute(InputCommand cmd)
    {
        switch (cmd)
        {
            case SwitchDesktopCommand s:
                {
                    if (!NativeMethods.SetThreadDesktop(s.NewDesktop))
                        _log.LogWarning("SetThreadDesktop failed for desktop {Name} (error {Error})", s.Name, Marshal.GetLastWin32Error());
                    if (s.OldDesktop != nint.Zero)
                        NativeMethods.CloseDesktop(s.OldDesktop);
                    break;
                }
            case MoveMouseCommand m:
                {
                    ExecuteMoveMouse(m.Dx, m.Dy);
                    break;
                }
            case MoveMouseRelativeCommand m:
                {
                    ExecuteMoveMouseRelative(m.Dx, m.Dy);
                    break;
                }
            case InjectKeyCommand k:
                {
                    ExecuteInjectKey(k.Msg);
                    break;
                }
            case InjectMouseButtonCommand b:
                {
                    ExecuteInjectMouseButton(b.Msg);
                    break;
                }
            case InjectMouseScrollCommand s:
                {
                    ExecuteInjectMouseScroll(s.Msg);
                    break;
                }
        }
    }

    private static unsafe void ExecuteMoveMouse(int dx, int dy)
    {
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

    private static unsafe void ExecuteMoveMouseRelative(int dx, int dy)
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

    private static unsafe void ExecuteInjectKey(KeyEventMessage msg)
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
                    inputs[0] = new INPUT { type = NativeMethods.INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = WinVirtualKey.LWin, dwFlags = NativeMethods.KEYEVENTF_EXTENDEDKEY } };
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

    private static unsafe void ExecuteInjectMouseButton(MouseButtonMessage msg)
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

    private static unsafe void ExecuteInjectMouseScroll(MouseScrollMessage msg)
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

    private static unsafe string GetDesktopName(nint hDesk)
    {
        if (hDesk == nint.Zero) return "";
        const int bufSize = 128;
        char* buf = stackalloc char[bufSize];
        return NativeMethods.GetUserObjectInformationW(hDesk, NativeMethods.UOI_NAME, (nint)buf, bufSize * sizeof(char), out _)
            ? new string(buf)
            : "";
    }
}

// -- command types --

abstract record InputCommand;
record MoveMouseCommand(int Dx, int Dy) : InputCommand;
record MoveMouseRelativeCommand(int Dx, int Dy) : InputCommand;
record InjectKeyCommand(KeyEventMessage Msg) : InputCommand;
record InjectMouseButtonCommand(MouseButtonMessage Msg) : InputCommand;
record InjectMouseScrollCommand(MouseScrollMessage Msg) : InputCommand;
record SwitchDesktopCommand(nint NewDesktop, nint OldDesktop, string Name) : InputCommand;
