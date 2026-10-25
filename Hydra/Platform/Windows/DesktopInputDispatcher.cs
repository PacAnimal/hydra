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
        WinVirtualKey.Numlock,  // MSDN: VK_NUMLOCK requires extended flag for correct toggle behavior
    ];

    private readonly ILogger _log;
    private readonly BlockingCollection<InputCommand> _queue = [];
    private readonly NumLockRestore _numLockRestore = new();
    private readonly Timer _pollTimer;
    private readonly Timer _relativeRestoreTimer;
    private readonly Lock _desktopLock = new();
    private Thread? _workerThread;
    private nint _activeDesktop;
    private string _activeDesktopName;
    private readonly Toggle _disposed = new();

    // tracks win key modifier usage to suppress accidental start menu on release
    private bool _winKeyDown;
    private bool _winUsedAsModifier;
    private bool _winInjected;  // Win key is currently logically down in Windows input state
    private ushort _bufferedWinVk = WinVirtualKey.LWin;  // which Win key was buffered (LWin or RWin)

    // Relative SendInput is accelerated by the user's mouse settings. Flatten once for a burst and restore
    // after a short idle period instead of issuing six SystemParametersInfo calls for every 125 Hz packet.
    private const int RelativeSettingsIdleMs = 100;
    private bool _relativeSettingsOverridden;
    private int _savedMouseThreshold1;
    private int _savedMouseThreshold2;
    private int _savedMouseAcceleration;
    private int _savedMouseSpeed;
    private long _lastRelativeMoveTick;

    internal DesktopInputDispatcher(ILogger log)
    {
        _log = log;
        _activeDesktop = NativeMethods.OpenInputDesktop(NativeMethods.DF_ALLOWOTHERACCOUNTHOOK, true, DesktopAccess);
        _activeDesktopName = WindowsDesktop.Name(_activeDesktop);
        if (_activeDesktop == nint.Zero)
            _log.LogWarning("OpenInputDesktop failed at startup (error {Error})", Marshal.GetLastWin32Error());
        else if (_log.IsEnabled(LogLevel.Information))
            _log.LogInformation("Desktop input dispatcher started, current desktop: {Name}", _activeDesktopName);
        StartWorker(_activeDesktop);
        _pollTimer = new Timer(_ => PollDesktop(), null, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200));
        _relativeRestoreTimer = new Timer(_ => TryQueue(new RestoreMouseSettingsCommand()), null,
            Timeout.Infinite, Timeout.Infinite);
    }

    internal void Dispatch(InputCommand cmd)
    {
        TryQueue(cmd);
    }

    public void Dispose()
    {
        if (!_disposed.TrySet()) return;
        _pollTimer.Dispose();
        _relativeRestoreTimer.Dispose();
        TryQueue(new RestoreMouseSettingsCommand(Force: true), duringDispose: true);
        _queue.CompleteAdding();
        _workerThread?.Join();
        _workerThread = null;
        lock (_desktopLock)
        {
            if (_activeDesktop != nint.Zero)
            {
                NativeMethods.CloseDesktop(_activeDesktop);
                _activeDesktop = nint.Zero;
            }
        }
    }

    private bool TryQueue(InputCommand command, bool duringDispose = false)
    {
        if (!duringDispose && _disposed) return false;
        try { return _queue.TryAdd(command); }
        catch (InvalidOperationException) { return false; }
    }

    private void StartWorker(nint hDesk)
    {
        _workerThread = new Thread(() =>
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
        _workerThread.Start();
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

        var name = WindowsDesktop.Name(hDesk);
        lock (_desktopLock)
        {
            if (_disposed)
            {
                NativeMethods.CloseDesktop(hDesk);
                return;
            }

            if (name == _activeDesktopName)
            {
                NativeMethods.CloseDesktop(hDesk);
                return;
            }

            if (_log.IsEnabled(LogLevel.Information)) _log.LogInformation("Input desktop changed: {Old} → {New}", _activeDesktopName, name);

            var oldDesk = _activeDesktop;

            // Re-attach the worker thread to the new desktop; close the old handle after it detaches.
            // If shutdown closed the queue between the checks, retain the old active handle and close
            // the newly-opened one here instead of leaking it.
            if (!TryQueue(new SwitchDesktopCommand(hDesk, oldDesk, name)))
            {
                NativeMethods.CloseDesktop(hDesk);
                return;
            }
            _activeDesktop = hDesk;
            _activeDesktopName = name;
        }
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
                    // stale Win key state from the old desktop must not bleed into the new one —
                    // a buffered _winKeyDown would cause the first key on the new desktop to fire a
                    // spurious Win+key shortcut (task view, desktop switch, etc.) on the new desktop.
                    _winKeyDown = false;
                    _winUsedAsModifier = false;
                    _winInjected = false;
                    _bufferedWinVk = WinVirtualKey.LWin;
                    break;
                }
            case MoveMouseCommand m:
                {
                    RestoreRelativeMouseSettings();
                    // drain any queued-up absolute moves — only the latest position matters.
                    // on lag recovery the relay may have buffered many moves; replaying every
                    // intermediate position causes a visible "zip around" effect.
                    while (_queue.TryTake(out var next))
                    {
                        if (next is MoveMouseCommand later) { m = later; continue; }
                        // hit a non-move command: flush our latest move first, then handle it
                        ExecuteMoveMouse(m.Dx, m.Dy).WarnIfFailed(_log, "mouse move");
                        Execute(next);
                        return;
                    }
                    ExecuteMoveMouse(m.Dx, m.Dy).WarnIfFailed(_log, "mouse move");
                    break;
                }
            case MoveMouseRelativeCommand m:
                {
                    ExecuteMoveMouseRelative(m.Dx, m.Dy).WarnIfFailed(_log, "mouse relative");
                    break;
                }
            case RestoreMouseSettingsCommand r:
                {
                    var idleFor = Environment.TickCount64 - Interlocked.Read(ref _lastRelativeMoveTick);
                    if (!r.Force && idleFor < RelativeSettingsIdleMs)
                    {
                        ScheduleRelativeRestore(RelativeSettingsIdleMs - idleFor);
                        break;
                    }
                    RestoreRelativeMouseSettings();
                    break;
                }
            case InjectKeyCommand k:
                {
                    ExecuteInjectKey(k.Msg).WarnIfFailed(_log, "key");
                    break;
                }
            case InjectMouseButtonCommand b:
                {
                    ExecuteInjectMouseButton(b.Msg).WarnIfFailed(_log, "mouse button");
                    break;
                }
            case InjectMouseScrollCommand s:
                {
                    ExecuteInjectMouseScroll(s.Msg);
                    break;
                }
            case RestoreLocksCommand:
                {
                    if (_numLockRestore.Take() is { } before)
                        SyncLockKey(WinVirtualKey.Numlock, before, extendedKey: true);
                    break;
                }
        }
    }

    private static uint ExecuteMoveMouse(int dx, int dy) =>
        InputSender.Send(InputSender.MouseInput(
            NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_VIRTUALDESK, dx, dy));

    private uint ExecuteMoveMouseRelative(int dx, int dy)
    {
        EnsureFlatRelativeMouseSettings();

        var result = InputSender.Send(InputSender.MouseInput(NativeMethods.MOUSEEVENTF_MOVE, dx, dy));
        Interlocked.Exchange(ref _lastRelativeMoveTick, Environment.TickCount64);
        ScheduleRelativeRestore(RelativeSettingsIdleMs);
        return result;
    }

    private void ScheduleRelativeRestore(long dueInMs)
    {
        try
        {
            _relativeRestoreTimer.Change(TimeSpan.FromMilliseconds(dueInMs), Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Shutdown raced the worker. Do not leave the user's global mouse settings flattened.
            RestoreRelativeMouseSettings();
        }
    }

    private unsafe void EnsureFlatRelativeMouseSettings()
    {
        if (_relativeSettingsOverridden) return;

        int* mouse = stackalloc int[3];
        var speed = 0;
        if (!NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETMOUSE, 0, (nint)mouse, 0)
            || !NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETMOUSESPEED, 0, (nint)(&speed), 0))
            return;

        _savedMouseThreshold1 = mouse[0];
        _savedMouseThreshold2 = mouse[1];
        _savedMouseAcceleration = mouse[2];
        _savedMouseSpeed = speed;

        if (!SetMouseThresholds(0, 0, 0) || !SetMouseSpeed(1))
        {
            // Best effort: a partial change is still restored from the snapshot immediately.
            RestoreRelativeMouseSettings(force: true);
            return;
        }

        _relativeSettingsOverridden = true;
    }

    private void RestoreRelativeMouseSettings(bool force = false)
    {
        if (!_relativeSettingsOverridden && !force) return;

        // both, whatever the first returns, so a restore puts back whatever it can
        SetMouseThresholds(_savedMouseThreshold1, _savedMouseThreshold2, _savedMouseAcceleration);
        SetMouseSpeed(_savedMouseSpeed);
        _relativeSettingsOverridden = false;
    }

    private static unsafe bool SetMouseThresholds(int threshold1, int threshold2, int acceleration)
    {
        int* mouse = stackalloc int[3];
        mouse[0] = threshold1;
        mouse[1] = threshold2;
        mouse[2] = acceleration;
        return NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETMOUSE, 0, (nint)mouse, 0);
    }

    private static bool SetMouseSpeed(int speed) =>
        NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETMOUSESPEED, 0, speed, 0);

    private uint ExecuteInjectKey(KeyEventMessage msg)
    {
        var isUp = msg.Type == KeyEventType.KeyUp;
        if (!isUp && msg.Key == SpecialKey.NumLock) _numLockRestore.Toggled();

        // sync CapsLock/NumLock state before injecting (skip when injecting the lock keys themselves)
        if (!isUp && msg.Key is not (SpecialKey.CapsLock or SpecialKey.NumLock))
            SyncLockState(msg.Modifiers, msg.Key?.IsNumLockSensitive() == true);

        if (msg.Character is { } ch)
        {
            var isAltGr = (msg.Modifiers & KeyModifiers.AltGr) != 0;
            var isSuper = (msg.Modifiers & KeyModifiers.Super) != 0;
            var shortcutContext = (msg.Modifiers & (KeyModifiers.Control | KeyModifiers.Super)) != 0;

            var scan = NativeMethods.VkKeyScanW(ch); // char implicit-converts to ushort
            // a shortcut has to arrive as WM_KEYDOWN carrying a real VK — KEYEVENTF_UNICODE delivers
            // WM_CHAR, which no app treats as a shortcut. when the active layout cannot map the char
            // (Cyrillic active, master sent a base-ASCII shortcut char) resolve the VK elsewhere.
            if (scan == -1 && shortcutContext) scan = ScanOutsideActiveLayout(ch);

            // use vk injection for all chars that map to a key+optional-shift combo on the slave's layout.
            // this gives correct key-hold semantics (GetKeyState works) and proper WM_KEYDOWN for shortcuts.
            // require Shift to be in msg.Modifiers when VkKeyScanW says Shift is needed, so exotic
            // cross-layout chars (unshifted on master, shifted on slave) don't produce the wrong VK.
            // AltGr compositions and unmappable chars fall back to atomic KEYEVENTF_UNICODE.
            // for chars that are unshifted on slave but master sent Shift, fall back to Unicode injection —
            // VK+Shift would produce the shifted character on slave, not the intended char.
            // exception: Ctrl/Super shortcuts always use VK injection (Shift is intentional there).
            var needsShift = (scan >> 8) == 1;
            var slaveUnshifted = (scan >> 8) == 0;
            var shiftMismatch = slaveUnshifted && (msg.Modifiers & KeyModifiers.Shift) != 0 && !shortcutContext;
            if (!isAltGr && scan != -1 && !shiftMismatch && (slaveUnshifted || (needsShift && (msg.Modifiers & KeyModifiers.Shift) != 0)))
            {
                var vk = (ushort)(scan & 0xFF);
                if (isSuper && vk == 0x4C) // Win+L: UIPI blocks SendInput; use the API directly
                {
                    if (!isUp) { _winKeyDown = false; _winUsedAsModifier = true; NativeMethods.LockWorkStation(); }
                    return 1;
                }

                if (isSuper && !isUp)
                {
                    _winUsedAsModifier = true;
                    if (!_winInjected)
                    {
                        // first shortcut key: batch Win down + key down atomically
                        _winInjected = true;
                        return InputSender.Send(InputSender.KeyInput(_bufferedWinVk, NativeMethods.KEYEVENTF_EXTENDEDKEY), InputSender.KeyInput(vk));
                    }
                    // Win already injected: just send the key
                    return InputSender.Key(vk);
                }
                else
                {
                    if (_winKeyDown) _winUsedAsModifier = true;
                    return InputSender.Key(vk, UpFlag(isUp));
                }
            }
            else
            {
                // AltGr or unmappable char — sent as a down+up pair (UnicodeTap).
                // don't flush Win for AltGr chars (they're not Win+key shortcuts), but DO mark Win
                // as used if Super is in mods — prevents a spurious Start menu tap on Win release.
                if (_winKeyDown && !isAltGr) FlushWin(isUp);
                if (_winKeyDown && isAltGr && isSuper) _winUsedAsModifier = true;
                if (isUp) return 1; // already released with the paired down event
                return InputSender.UnicodeTap(ch);
            }
        }
        else if (msg.Key is SpecialKey.MoveToBeginningOfLine or SpecialKey.MoveToEndOfLine)
        {
            var vk = msg.Key == SpecialKey.MoveToBeginningOfLine ? WinVirtualKey.Home : WinVirtualKey.End;
            if (_winKeyDown) FlushWin(isUp);
            return InputSender.Key(vk, UpFlag(isUp) | NativeMethods.KEYEVENTF_EXTENDEDKEY);
        }
        else if (msg.Key == SpecialKey.MissionControl)
        {
            if (!isUp)
            {
                if (_winKeyDown) { _winUsedAsModifier = true; _winKeyDown = false; }
                // Win+Tab = Task View
                return InputSender.Send(
                    InputSender.KeyInput(WinVirtualKey.LWin, NativeMethods.KEYEVENTF_EXTENDEDKEY),
                    InputSender.KeyInput(WinVirtualKey.Tab),
                    InputSender.KeyInput(WinVirtualKey.Tab, NativeMethods.KEYEVENTF_KEYUP),
                    InputSender.KeyInput(WinVirtualKey.LWin, NativeMethods.KEYEVENTF_EXTENDEDKEY | NativeMethods.KEYEVENTF_KEYUP));
            }
        }
        else if (msg.Key == SpecialKey.AltGr)
        {
            // AltGr on Windows = synthetic LCtrl + RMenu. send both so apps that check
            // GetKeyState(VK_CONTROL) or look for the Ctrl+Alt combination see the correct state.
            return InputSender.Send(
                InputSender.KeyInput(WinVirtualKey.LControl, UpFlag(isUp)),
                InputSender.KeyInput(WinVirtualKey.RMenu, UpFlag(isUp) | NativeMethods.KEYEVENTF_EXTENDEDKEY));
        }
        else if (msg.Key == SpecialKey.KP_Enter)
        {
            // numpad enter = VK_RETURN + extended key
            if (_winKeyDown) FlushWin(isUp);
            return InputSender.Key(WinVirtualKey.Return, UpFlag(isUp) | NativeMethods.KEYEVENTF_EXTENDEDKEY);
        }
        else if (msg.Key is SpecialKey.KP_Tab or SpecialKey.KP_Space)
        {
            // no dedicated numpad Tab/Space VK on Windows — inject as regular Tab/Space
            if (_winKeyDown) FlushWin(isUp);
            var vkTabSpace = msg.Key == SpecialKey.KP_Tab ? WinVirtualKey.Tab : WinVirtualKey.Space;
            return InputSender.Key(vkTabSpace, UpFlag(isUp));
        }
        else if (msg.Key == SpecialKey.KP_Equal)
        {
            // no dedicated numpad = key on Windows; inject as unicode down+up
            if (_winKeyDown) FlushWin(isUp);
            if (isUp) return 1;
            return InputSender.UnicodeTap('=');
        }
        else if (msg.Key is { } key && WinSpecialKeyMap.Instance.Reverse.TryGetValue(key.WithoutKeypad(), out var vk))
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
                    _bufferedWinVk = (ushort)vk;
                    _winKeyDown = true;
                    return 1;
                }
                else
                {
                    _winKeyDown = false;
                    if (!_winUsedAsModifier)
                    {
                        // bare win tap — win was never injected, so inject down+up now to open start menu
                        _winInjected = false;
                        return InputSender.VkTap((int)vk, NativeMethods.KEYEVENTF_EXTENDEDKEY);
                    }
                    _winUsedAsModifier = false;
                    if (!_winInjected) return 1; // Win was never logically injected (e.g. Win+L handled it directly)
                    _winInjected = false;
                    // fall through to inject Win up
                }
            }

            // flush buffered Win key before injecting a non-Win special key
            if (_winKeyDown && !isWin) FlushWin(isUp);

            var flags = UpFlag(isUp);
            // a keypad navigation key is its dedicated twin's VK without the extended flag
            if (ExtendedKeys.Contains(vk) && key.WithoutKeypad() == key)
                flags |= NativeMethods.KEYEVENTF_EXTENDEDKEY;
            return InputSender.Key((int)vk, flags);
        }

        return 1; // nothing to inject
    }

    private static uint UpFlag(bool isUp) => isUp ? NativeMethods.KEYEVENTF_KEYUP : 0u;

    private void SyncLockState(KeyModifiers mods, bool numLock)
    {
        SyncLockKey(WinVirtualKey.Capital, want: (mods & KeyModifiers.CapsLock) != 0, extendedKey: false);
        var wantNumLock = (mods & KeyModifiers.NumLock) != 0;
        if (numLock && SyncLockKey(WinVirtualKey.Numlock, wantNumLock, extendedKey: true))
            _numLockRestore.Synced(had: !wantNumLock);
    }

    // returns whether it flipped the lock
    private bool SyncLockKey(int vk, bool want, bool extendedKey)
    {
        var have = (NativeMethods.GetKeyState(vk) & 0x01) != 0;
        if (have == want) return false;
        InputSender.VkTap(vk, extendedKey ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0u).WarnIfFailed(_log, "lock key");
        return true;
    }

    // injects the buffered Win down (unless a shortcut already did) and marks Win as used as a modifier.
    // called whenever a key arrives while _winKeyDown is set, to flush the buffered Win press.
    private void FlushWin(bool isUp)
    {
        if (!_winUsedAsModifier && !isUp)
        {
            InputSender.Key(_bufferedWinVk, NativeMethods.KEYEVENTF_EXTENDEDKEY).WarnIfFailed(_log, "Win");
            _winInjected = true;
        }
        _winUsedAsModifier = true;
    }

    private static uint ExecuteInjectMouseButton(MouseButtonMessage msg)
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

        return InputSender.Send(InputSender.MouseInput(msg.IsPressed ? downFlag : upFlag, mouseData: mouseData));
    }

    private void ExecuteInjectMouseScroll(MouseScrollMessage msg)
    {
        if (msg.YDelta != 0)
            InputSender.Send(InputSender.MouseInput(NativeMethods.MOUSEEVENTF_WHEEL, mouseData: (uint)msg.YDelta)).WarnIfFailed(_log, "scroll y");
        if (msg.XDelta != 0)
            InputSender.Send(InputSender.MouseInput(NativeMethods.MOUSEEVENTF_HWHEEL, mouseData: (uint)msg.XDelta)).WarnIfFailed(_log, "scroll x");
    }

    // resolves a char to a VK+shift pair without using the active layout, for shortcut injection only.
    // tries every layout loaded in the session, then falls back to the VK codes the standard range
    // fixes for ASCII (VK_A..VK_Z == 'A'..'Z', VK_0..VK_9 == '0'..'9') so a slave with no Latin
    // layout installed at all still gets working shortcuts.
    private static unsafe short ScanOutsideActiveLayout(char ch)
    {
        var count = NativeMethods.GetKeyboardLayoutList(0, null);
        if (count > 0)
        {
            var layouts = stackalloc nint[count];
            count = NativeMethods.GetKeyboardLayoutList(count, layouts);
            for (var i = 0; i < count; i++)
            {
                var scan = NativeMethods.VkKeyScanExW(ch, layouts[i]);
                if (scan != -1) return scan;
            }
        }

        // high byte 0 = no Shift required, which is what a shortcut char wants
        var upper = char.ToUpperInvariant(ch);
        return upper switch
        {
            >= 'A' and <= 'Z' => (short)upper,
            >= '0' and <= '9' => (short)upper,
            _ => -1,
        };
    }
}

// -- command types --

abstract record InputCommand;
record MoveMouseCommand(int Dx, int Dy) : InputCommand;
record MoveMouseRelativeCommand(int Dx, int Dy) : InputCommand;
record InjectKeyCommand(KeyEventMessage Msg) : InputCommand;
record InjectMouseButtonCommand(MouseButtonMessage Msg) : InputCommand;
record InjectMouseScrollCommand(MouseScrollMessage Msg) : InputCommand;
record RestoreLocksCommand : InputCommand;
record RestoreMouseSettingsCommand(bool Force = false) : InputCommand;
record SwitchDesktopCommand(nint NewDesktop, nint OldDesktop, string Name) : InputCommand;
