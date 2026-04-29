using Cathedral.Utils;
using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Relay;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Linux;

public sealed class XorgOutputHandler : IPlatformOutput, ICursor
{
    private bool _cursorHidden;
    private readonly nint _display;
    private readonly int _screen;
    private readonly nint _rootWindow;
    private readonly Toggle _disposed = new();
    private readonly Queue<int> _unusedKeycodes = [];
    private readonly Dictionary<ulong, int> _tempBindings = [];
    private readonly Dictionary<ulong, uint> _naturalHeld = [];  // keysym → keycode injected via natural path (stable key-up even after layout switch)
    private readonly Lock _lock = new();                 // protects _tempBindings, _unusedKeycodes, _naturalHeld
    private readonly ILogger<XorgOutputHandler> _log;
    private int _keysymsPerKeycode = 4;  // actual server value; overridden in FindUnusedKeycodes
    private ulong[] _emptyCharSlots = [0UL, 0UL, 0UL, 0UL];  // pre-allocated; sized after FindUnusedKeycodes

    public XorgOutputHandler(ILogger<XorgOutputHandler> log)
    {
        _log = log;
        _ = NativeMethods.XInitThreads();
        _display = NativeMethods.XOpenDisplay(null);
        if (_display == nint.Zero)
            throw new InvalidOperationException("XOpenDisplay failed — is DISPLAY set?");

        _screen = NativeMethods.XDefaultScreen(_display);
        _rootWindow = NativeMethods.XDefaultRootWindow(_display);
        // allow XTest events during active grabs (e.g. fullscreen games)
        _ = NativeMethods.XTestGrabControl(_display, true);
        FindUnusedKeycodes();
    }

    private unsafe void FindUnusedKeycodes()
    {
        _ = NativeMethods.XDisplayKeycodes(_display, out var minKeycode, out var maxKeycode);
        var count = maxKeycode - minKeycode + 1;
        var map = NativeMethods.XGetKeyboardMapping(_display, (uint)minKeycode, count, out var keysymsPerKeycode);
        if (map == nint.Zero) return;

        _keysymsPerKeycode = keysymsPerKeycode;
        _emptyCharSlots = new ulong[_keysymsPerKeycode];

        var keysyms = (ulong*)map;
        for (var i = 0; i < count; i++)
        {
            var allEmpty = true;
            for (var j = 0; j < keysymsPerKeycode; j++)
            {
                if (keysyms[i * keysymsPerKeycode + j] != 0) { allEmpty = false; break; }
            }
            if (allEmpty)
                _unusedKeycodes.Enqueue(minKeycode + i);
        }

        _ = NativeMethods.XFree(map);
    }

    public void MoveMouse(int x, int y)
    {
        _ = NativeMethods.XTestFakeMotionEvent(_display, _screen, x, y, 0);
        _ = NativeMethods.XFlush(_display);
    }

    public void MoveMouseRelative(int dx, int dy)
    {
        // XTestFakeRelativeMotionEvent generates a proper raw input event (XI_RawMotion),
        // which games see. XWarpPointer is a cursor warp only and is invisible to raw input.
        _ = NativeMethods.XTestFakeRelativeMotionEvent(_display, dx, dy, 0);
        _ = NativeMethods.XFlush(_display);
    }

    // tracks expected lock-key state so rapid events don't see stale XkbGetState results
    private byte? _pendingLockedMods;

    public void InjectKey(KeyEventMessage msg)
    {
        var isDown = msg.Type == KeyEventType.KeyDown;

        // sync CapsLock/NumLock state before injecting (skip when injecting the lock keys themselves)
        if (isDown && msg.Key is not (SpecialKey.CapsLock or SpecialKey.NumLock))
            SyncLockState(msg.Modifiers);

        if (msg.Character is { } ch)
        {
            // unicode char → keysym, always injected via temp-binding at all 4 levels so the
            // produced character is independent of the slave's keyboard layout and current modifier state.
            var keysym = ch <= '\xFF' ? (ulong)ch : 0x01000000u | ch;
            InjectCharKeysym(keysym, isDown, msg.Modifiers);
        }
        else if (msg.Key is SpecialKey.MoveToBeginningOfLine or SpecialKey.MoveToEndOfLine)
        {
            var keysym = msg.Key == SpecialKey.MoveToBeginningOfLine ? XorgVirtualKey.Home : XorgVirtualKey.End;
            InjectKeysym(keysym, isDown);
        }
        else if (msg.Key == SpecialKey.MissionControl)
        {
            // tap Super_L to trigger GNOME Activities / KDE Overview
            if (!isDown) return;
            var keycode = NativeMethods.XKeysymToKeycode(_display, XorgVirtualKey.Super_L);
            if (keycode == 0) return;
            _ = NativeMethods.XTestFakeKeyEvent(_display, keycode, true, 0);
            _ = NativeMethods.XTestFakeKeyEvent(_display, keycode, false, 0);
            _ = NativeMethods.XFlush(_display);
        }
        else if (msg.Key is { } key)
        {
            var keysym = SpecialKeyToKeysym(key);
            if (keysym != 0)
                InjectKeysym(keysym, isDown);
            // invalidate tracked state so next sync re-queries from server
            if (key is SpecialKey.CapsLock or SpecialKey.NumLock)
                _pendingLockedMods = null;
        }
    }

    public void InjectMouseButton(MouseButtonMessage msg)
    {
        var button = msg.Button switch
        {
            MouseButton.Left => 1u,
            MouseButton.Middle => 2u,
            MouseButton.Right => 3u,
            MouseButton.Extra1 => 8u,
            MouseButton.Extra2 => 9u,
            _ => 0u,
        };
        if (button == 0) return;

        _ = NativeMethods.XTestFakeButtonEvent(_display, button, msg.IsPressed, 0);
        _ = NativeMethods.XFlush(_display);
    }

    public void InjectMouseScroll(MouseScrollMessage msg)
    {
        // x11 scroll: buttons 4=up, 5=down, 6=left, 7=right (each press = one 120-unit click)
        InjectScrollAxis(4u, 5u, msg.YDelta / 120);
        InjectScrollAxis(7u, 6u, msg.XDelta / 120);
        _ = NativeMethods.XFlush(_display);
    }

    private void InjectScrollAxis(uint positiveButton, uint negativeButton, int clicks)
    {
        if (clicks == 0) return;
        var button = clicks > 0 ? positiveButton : negativeButton;
        var n = Math.Abs(clicks);
        for (var i = 0; i < n; i++)
        {
            _ = NativeMethods.XTestFakeButtonEvent(_display, button, true, 0);
            _ = NativeMethods.XTestFakeButtonEvent(_display, button, false, 0);
        }
    }

    // inject a character keysym. prefers using the key's natural keycode for game/scancode
    // compatibility. on key-down, stores (keysym → keycode) in _naturalHeld so key-up uses the
    // same keycode even if the XKB layout changes between press and release.
    private void InjectCharKeysym(ulong keysym, bool isDown, KeyModifiers modifiers = KeyModifiers.None)
    {
        lock (_lock)
        {
            // on key-up: use the keycode stored at key-down time (not re-resolved — layout may have changed)
            if (!isDown && _naturalHeld.Remove(keysym, out var heldKeycode))
            {
                _ = NativeMethods.XTestFakeKeyEvent(_display, heldKeycode, false, 0);
                _ = NativeMethods.XFlush(_display);
                return;
            }

            var naturalKeycode = NativeMethods.XKeysymToKeycode(_display, keysym);

            // skip natural keycode path if the keysym is currently temp-bound: that means key-down
            // already used a temp keycode and key-up must release the same one.
            // AltGr guard is key-down only: on key-up the _naturalHeld check above already handled it.
            if (naturalKeycode != 0 && !_tempBindings.ContainsKey(keysym) && (!isDown || (modifiers & KeyModifiers.AltGr) == 0))
            {
                // query effective XKB group (layout) so multi-group setups (e.g. Cyrillic+Latin switcher) use
                // the correct group when checking whether the keysym lives on the natural keycode.
                var activeGroup = NativeMethods.XkbGetState(_display, NativeMethods.XkbUseCoreKbd, out var xkbState) == 0
                    ? xkbState.Group
                    : 0;

                // level 0: unshifted base — safe regardless of held modifiers
                if (NativeMethods.XkbKeycodeToKeysym(_display, naturalKeycode, activeGroup, 0) == keysym)
                {
                    if (isDown) _naturalHeld[keysym] = naturalKeycode;
                    _ = NativeMethods.XTestFakeKeyEvent(_display, naturalKeycode, isDown, 0);
                    _ = NativeMethods.XFlush(_display);
                    return;
                }

                // level 1 (Shift variant): on key-down require Shift to be held.
                // CapsLock is intentionally excluded: slave CapsLock state may not match master's at startup,
                // so chars uppercase-via-CapsLock fall through to temp-binding (which is CapsLock-independent).
                if (NativeMethods.XkbKeycodeToKeysym(_display, naturalKeycode, activeGroup, 1) == keysym &&
                    (!isDown || (modifiers & KeyModifiers.Shift) != 0))
                {
                    if (isDown) _naturalHeld[keysym] = naturalKeycode;
                    _ = NativeMethods.XTestFakeKeyEvent(_display, naturalKeycode, isDown, 0);
                    _ = NativeMethods.XFlush(_display);
                    return;
                }
            }

            // keysym not at level 0/1 of natural keycode — temp-bind at all levels so modifier state doesn't matter
            TempBind(keysym, isDown);
        }
    }

    // the fast path below does not acquire _lock — _lock only protects _tempBindings/_unusedKeycodes/_naturalHeld,
    // none of which the fast path accesses (it only calls XKeysymToKeycode and XTestFakeKeyEvent).
    private void InjectKeysym(ulong keysym, bool isDown)
    {
        var keycode = NativeMethods.XKeysymToKeycode(_display, keysym);
        if (keycode != 0)
        {
            _ = NativeMethods.XTestFakeKeyEvent(_display, keycode, isDown, 0);
            _ = NativeMethods.XFlush(_display);
            return;
        }

        lock (_lock)
        {
            TempBind(keysym, isDown);
        }
    }

    // temporarily binds keysym to an unused keycode for injection, then unbinds on key-up.
    // fills all levels with the same keysym so modifier state on the slave doesn't affect the result.
    private void TempBind(ulong keysym, bool isDown)
    {
        if (isDown)
        {
            // clear any existing binding for this keysym first (returns its keycode to the pool)
            if (_tempBindings.TryGetValue(keysym, out var stale))
            {
                // release stale key before unmapping to avoid stuck key
                _ = NativeMethods.XTestFakeKeyEvent(_display, (uint)stale, false, 0);
                _ = NativeMethods.XFlush(_display);
                _ = NativeMethods.XChangeKeyboardMapping(_display, stale, _keysymsPerKeycode, _emptyCharSlots, 1);
                _ = NativeMethods.XSync(_display, false);
                _unusedKeycodes.Enqueue(stale);
            }
            if (_unusedKeycodes.Count == 0)
            {
                _log.LogWarning("No unused keycodes available — dropping keysym 0x{Keysym:X}", keysym);
                return;
            }
            var tempKeycode = _unusedKeycodes.Dequeue();
            var filledSlots = new ulong[_keysymsPerKeycode];
            Array.Fill(filledSlots, keysym);
            _ = NativeMethods.XChangeKeyboardMapping(_display, tempKeycode, _keysymsPerKeycode, filledSlots, 1);
            _ = NativeMethods.XSync(_display, false);
            _ = NativeMethods.XTestFakeKeyEvent(_display, (uint)tempKeycode, true, 0);
            _ = NativeMethods.XFlush(_display);
            _tempBindings[keysym] = tempKeycode;
        }
        else
        {
            if (!_tempBindings.TryGetValue(keysym, out var tempKeycode)) return;
            _ = NativeMethods.XTestFakeKeyEvent(_display, (uint)tempKeycode, false, 0);
            _ = NativeMethods.XFlush(_display);
            _ = NativeMethods.XChangeKeyboardMapping(_display, tempKeycode, _keysymsPerKeycode, _emptyCharSlots, 1);
            _ = NativeMethods.XSync(_display, false);
            _unusedKeycodes.Enqueue(tempKeycode);
            _tempBindings.Remove(keysym);
        }
    }

    private static ulong SpecialKeyToKeysym(SpecialKey key)
    {
        // media keys and other non-MISCELLANY keys: reverse map via XorgSpecialKeyMap
        if (XorgSpecialKeyMap.Instance.Reverse.TryGetValue(key, out var keysym))
            return keysym;

        // MISCELLANY keys: SpecialKey value encodes (keysym | 0x01000000), strip the flag
        var raw = (uint)key;
        if ((raw & 0xFF000000u) == 0x01000000u)
            return raw & 0x00FFFFFFu;

        return 0;
    }

    private void SyncLockState(KeyModifiers mods)
    {
        byte lockedMods;
        if (_pendingLockedMods.HasValue)
        {
            lockedMods = _pendingLockedMods.Value;
        }
        else
        {
            if (NativeMethods.XkbGetState(_display, NativeMethods.XkbUseCoreKbd, out var xkbState) != 0) return;
            lockedMods = xkbState.LockedMods;
        }

        SyncLockKey(XorgVirtualKey.CapsLock, NativeMethods.LockMask, (mods & KeyModifiers.CapsLock) != 0, ref lockedMods);
        SyncLockKey(XorgVirtualKey.NumLock, NativeMethods.Mod2Mask, (mods & KeyModifiers.NumLock) != 0, ref lockedMods);
        _pendingLockedMods = lockedMods;
    }

    private void SyncLockKey(ulong keysym, uint lockMask, bool want, ref byte lockedMods)
    {
        var have = (lockedMods & lockMask) != 0;
        if (have == want) return;
        var keycode = NativeMethods.XKeysymToKeycode(_display, keysym);
        if (keycode == 0) return;
        _ = NativeMethods.XTestFakeKeyEvent(_display, keycode, true, 0);
        _ = NativeMethods.XTestFakeKeyEvent(_display, keycode, false, 0);
        _ = NativeMethods.XFlush(_display);
        lockedMods = want ? (byte)(lockedMods | lockMask) : (byte)(lockedMods & ~lockMask);
    }

    public ValueTask HideCursor()
    {
        if (_cursorHidden) return ValueTask.CompletedTask;
        NativeMethods.XFixesHideCursor(_display, _rootWindow);
        _ = NativeMethods.XFlush(_display);
        _cursorHidden = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask ShowCursor()
    {
        if (!_cursorHidden) return ValueTask.CompletedTask;
        NativeMethods.XFixesShowCursor(_display, _rootWindow);
        _ = NativeMethods.XFlush(_display);
        _cursorHidden = false;
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        if (!_disposed.TrySet()) return;
        if (_display != nint.Zero)
        {
            lock (_lock)
            {
                foreach (var (_, kc) in _naturalHeld)
                    if (kc != 0) _ = NativeMethods.XTestFakeKeyEvent(_display, kc, false, 0);
                foreach (var tempKeycode in _tempBindings.Values)
                {
                    _ = NativeMethods.XTestFakeKeyEvent(_display, (uint)tempKeycode, false, 0);
                    _ = NativeMethods.XFlush(_display);
                    _ = NativeMethods.XChangeKeyboardMapping(_display, tempKeycode, _keysymsPerKeycode, _emptyCharSlots, 1);
                }
                if (_naturalHeld.Count > 0 || _tempBindings.Count > 0)
                    _ = NativeMethods.XSync(_display, false);
            }
            if (_cursorHidden)
                NativeMethods.XFixesShowCursor(_display, _rootWindow);
            _ = NativeMethods.XCloseDisplay(_display);
        }
    }
}
