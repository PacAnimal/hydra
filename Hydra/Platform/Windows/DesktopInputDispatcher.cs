using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Cathedral.Utils;
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
    private const uint DesktopAccess = NativeMethods.DESKTOP_CREATEWINDOW | NativeMethods.DESKTOP_HOOKCONTROL
                                     | NativeMethods.DESKTOP_READOBJECTS | NativeMethods.GENERIC_WRITE;

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
    private readonly Toggle _disposed = new();

    // tracks win key modifier usage to suppress accidental start menu on release
    private bool _winKeyDown;
    private bool _winUsedAsModifier;

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
        if (!_disposed.TrySet()) return;
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
                    // drain any queued-up absolute moves — only the latest position matters.
                    // on lag recovery the relay may have buffered many moves; replaying every
                    // intermediate position causes a visible "zip around" effect.
                    while (_queue.TryTake(out var next))
                    {
                        if (next is MoveMouseCommand later) { m = later; continue; }
                        // hit a non-move command: flush our latest move first, then handle it
                        if (ExecuteMoveMouse(m.Dx, m.Dy) == 0)
                            _log.LogWarning("SendInput(mouse move) failed (error {Error})", Marshal.GetLastWin32Error());
                        Execute(next);
                        return;
                    }
                    if (ExecuteMoveMouse(m.Dx, m.Dy) == 0)
                        _log.LogWarning("SendInput(mouse move) failed (error {Error})", Marshal.GetLastWin32Error());
                    break;
                }
            case MoveMouseRelativeCommand m:
                {
                    if (ExecuteMoveMouseRelative(m.Dx, m.Dy) == 0)
                        _log.LogWarning("SendInput(mouse relative) failed (error {Error})", Marshal.GetLastWin32Error());
                    break;
                }
            case InjectKeyCommand k:
                {
                    if (ExecuteInjectKey(k.Msg) == 0)
                        _log.LogWarning("SendInput(key) failed (error {Error})", Marshal.GetLastWin32Error());
                    break;
                }
            case InjectMouseButtonCommand b:
                {
                    if (ExecuteInjectMouseButton(b.Msg) == 0)
                        _log.LogWarning("SendInput(mouse button) failed (error {Error})", Marshal.GetLastWin32Error());
                    break;
                }
            case InjectMouseScrollCommand s:
                {
                    ExecuteInjectMouseScroll(s.Msg);
                    break;
                }
        }
    }

    private static unsafe uint ExecuteMoveMouse(int dx, int dy)
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
        return NativeMethods.SendInput(1, &input, sizeof(INPUT));
    }

    private static unsafe uint ExecuteMoveMouseRelative(int dx, int dy)
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
        var result = NativeMethods.SendInput(1, &input, sizeof(INPUT));

        if (saved)
        {
            NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETMOUSE, 0, (nint)oldMouse, 0);
            NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETMOUSESPEED, 0, oldSpeed, 0);
        }

        return result;
    }

    private unsafe uint ExecuteInjectKey(KeyEventMessage msg)
    {
        var isUp = msg.Type == KeyEventType.KeyUp;

        if (msg.Character is { } ch)
        {
            var scan = NativeMethods.VkKeyScanW(ch); // char implicit-converts to ushort
            var isAltGr = (msg.Modifiers & KeyModifiers.AltGr) != 0;
            var isSuper = (msg.Modifiers & KeyModifiers.Super) != 0;

            // use vk injection for all chars that map to a key+optional-shift combo on the slave's layout.
            // this gives correct key-hold semantics (GetKeyState works) and proper WM_KEYDOWN for shortcuts.
            // AltGr compositions and unmappable chars fall back to atomic KEYEVENTF_UNICODE.
            if (!isAltGr && scan != -1 && (scan >> 8) is 0 or 1)
            {
                var vk = (ushort)(scan & 0xFF);
                if (isSuper && vk == 0x4C) // Win+L: UIPI blocks SendInput; use the API directly
                {
                    if (!isUp) NativeMethods.LockWorkStation();
                    return 1;
                }

                if (isSuper && !isUp)
                {
                    // batch Win down + key down in one SendInput so the shell sees them atomically
                    _winUsedAsModifier = true;
                    var inputs = stackalloc INPUT[2];
                    inputs[0] = new INPUT { type = NativeMethods.INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = WinVirtualKey.LWin, dwFlags = NativeMethods.KEYEVENTF_EXTENDEDKEY } };
                    inputs[1] = new INPUT { type = NativeMethods.INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = vk } };
                    return NativeMethods.SendInput(2, inputs, sizeof(INPUT));
                }
                else
                {
                    if (_winKeyDown) _winUsedAsModifier = true;
                    var flags = isUp ? NativeMethods.KEYEVENTF_KEYUP : 0u;
                    var input = new INPUT
                    {
                        type = NativeMethods.INPUT_KEYBOARD,
                        ki = new KEYBDINPUT { wVk = vk, dwFlags = flags },
                    };
                    return NativeMethods.SendInput(1, &input, sizeof(INPUT));
                }
            }
            else
            {
                // AltGr or unmappable char — send down+up atomically to avoid VK_PACKET overlap.
                // all KEYEVENTF_UNICODE events share VK_PACKET; overlapping downs for different chars
                // cause Windows to retype the first char's scan code instead of the second.
                if (_winKeyDown) _winUsedAsModifier = true;
                if (isUp) return 1; // already released with the paired down event
                var inputs = stackalloc INPUT[2];
                inputs[0] = new INPUT { type = NativeMethods.INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = NativeMethods.KEYEVENTF_UNICODE } };
                inputs[1] = new INPUT { type = NativeMethods.INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP } };
                return NativeMethods.SendInput(2, inputs, sizeof(INPUT));
            }
        }
        else if (msg.Key is SpecialKey.MoveToBeginningOfLine or SpecialKey.MoveToEndOfLine)
        {
            var vk = msg.Key == SpecialKey.MoveToBeginningOfLine ? WinVirtualKey.Home : WinVirtualKey.End;
            var flags = (isUp ? NativeMethods.KEYEVENTF_KEYUP : 0u) | NativeMethods.KEYEVENTF_EXTENDEDKEY;
            var input = new INPUT { type = NativeMethods.INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = (ushort)vk, dwFlags = flags } };
            return NativeMethods.SendInput(1, &input, sizeof(INPUT));
        }
        else if (msg.Key == SpecialKey.MissionControl)
        {
            if (!isUp)
            {
                // Win+Tab = Task View
                var inputs = stackalloc INPUT[4];
                inputs[0] = new INPUT { type = NativeMethods.INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = WinVirtualKey.LWin, dwFlags = NativeMethods.KEYEVENTF_EXTENDEDKEY } };
                inputs[1] = new INPUT { type = NativeMethods.INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = WinVirtualKey.Tab } };
                inputs[2] = new INPUT { type = NativeMethods.INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = WinVirtualKey.Tab, dwFlags = NativeMethods.KEYEVENTF_KEYUP } };
                inputs[3] = new INPUT { type = NativeMethods.INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = WinVirtualKey.LWin, dwFlags = NativeMethods.KEYEVENTF_EXTENDEDKEY | NativeMethods.KEYEVENTF_KEYUP } };
                return NativeMethods.SendInput(4, inputs, sizeof(INPUT));
            }
        }
        else if (msg.Key is { } key && WinSpecialKeyMap.Instance.Reverse.TryGetValue(key, out var vk))
        {
            var isWin = vk == WinVirtualKey.LWin || vk == WinVirtualKey.RWin;
            if (isWin)
            {
                if (!isUp)
                {
                    // buffer the Win down — only inject it paired with a shortcut key (see character path above).
                    // sending a standalone LWin down means the shell sees Win held with nothing between
                    // down and up, and opens the start menu on release even after a shortcut was used.
                    // don't reset _winUsedAsModifier on repeats or it'll clear the flag set by the shortcut key
                    if (!_winKeyDown) _winUsedAsModifier = false;
                    _winKeyDown = true;
                    return 1;
                }
                else
                {
                    _winKeyDown = false;
                    if (!_winUsedAsModifier)
                    {
                        // bare win tap — win down was never injected, so inject down+up now to open start menu
                        _winUsedAsModifier = false;
                        var inputs = stackalloc INPUT[2];
                        inputs[0] = new INPUT { type = NativeMethods.INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = (ushort)vk, dwFlags = NativeMethods.KEYEVENTF_EXTENDEDKEY } };
                        inputs[1] = new INPUT { type = NativeMethods.INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = (ushort)vk, dwFlags = NativeMethods.KEYEVENTF_EXTENDEDKEY | NativeMethods.KEYEVENTF_KEYUP } };
                        return NativeMethods.SendInput(2, inputs, sizeof(INPUT));
                    }
                    _winUsedAsModifier = false;
                    // fall through to inject Win up normally (Win down was already sent with the shortcut batch)
                }
            }

            var flags = isUp ? NativeMethods.KEYEVENTF_KEYUP : 0u;
            if (ExtendedKeys.Contains(vk))
                flags |= NativeMethods.KEYEVENTF_EXTENDEDKEY;

            var input = new INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                ki = new KEYBDINPUT { wVk = (ushort)vk, dwFlags = flags },
            };
            return NativeMethods.SendInput(1, &input, sizeof(INPUT));
        }

        return 1; // nothing to inject
    }

    private static unsafe uint ExecuteInjectMouseButton(MouseButtonMessage msg)
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
        if (downFlag == 0) return 1;

        var input = new INPUT
        {
            type = NativeMethods.INPUT_MOUSE,
            mi = new MOUSEINPUT
            {
                dwFlags = msg.IsPressed ? downFlag : upFlag,
                mouseData = mouseData,
            },
        };
        return NativeMethods.SendInput(1, &input, sizeof(INPUT));
    }

    private unsafe void ExecuteInjectMouseScroll(MouseScrollMessage msg)
    {
        if (msg.YDelta != 0)
        {
            var input = new INPUT
            {
                type = NativeMethods.INPUT_MOUSE,
                mi = new MOUSEINPUT { dwFlags = NativeMethods.MOUSEEVENTF_WHEEL, mouseData = (uint)msg.YDelta },
            };
            if (NativeMethods.SendInput(1, &input, sizeof(INPUT)) == 0)
                _log.LogWarning("SendInput(scroll y) failed (error {Error})", Marshal.GetLastWin32Error());
        }

        if (msg.XDelta != 0)
        {
            var input = new INPUT
            {
                type = NativeMethods.INPUT_MOUSE,
                mi = new MOUSEINPUT { dwFlags = NativeMethods.MOUSEEVENTF_HWHEEL, mouseData = (uint)msg.XDelta },
            };
            if (NativeMethods.SendInput(1, &input, sizeof(INPUT)) == 0)
                _log.LogWarning("SendInput(scroll x) failed (error {Error})", Marshal.GetLastWin32Error());
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
