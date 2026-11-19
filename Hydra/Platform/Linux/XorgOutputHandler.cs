using Cathedral.Utils;
using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Relay;
using Hydra.Screen;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Linux;

public sealed class XorgOutputHandler : IPlatformOutput, ICursor
{
    private bool _cursorHidden;
    private readonly nint _display;
    private readonly int _screen;
    private readonly nint _rootWindow;
    private readonly Toggle _disposed = new();
    // every keycode empty at startup; written only by the constructor, so read without _lock. none of them is ever a
    // keysym's natural keycode, since whatever it holds is our own temp binding
    private readonly HashSet<uint> _spareKeycodes = [];
    private int _minKeycode;
    private int _maxKeycode;
    private readonly Queue<int> _unusedKeycodes = [];    // spare keycodes with nothing bound
    // keysym → the spare keycode it is bound to, and when it was last pressed (for LRU eviction). a binding outlives
    // its key: a client (GTK4) may fetch the keymap only when it handles the mapping notify, after the key event,
    // and must still find the keysym there, so a binding is undone only to make room or on Dispose. bindings outlive a
    // crash too: a killed slave leaves stray keysyms on high keycodes and the next start has fewer spares, harmlessly
    private readonly Dictionary<ulong, (int Keycode, long LastUse)> _tempBound = [];
    private readonly HashSet<ulong> _tempHeld = [];      // keysyms currently held down on their temp keycode
    private long _tempUses;
    // keysym → the natural keycode a character or special key went down on, so its key-up and ReleaseHeld let go of
    // that keycode even after the layout or group changed under it
    private readonly Dictionary<ulong, uint> _naturalHeld = [];
    // held keysym (natural or temp) → the press it belongs to. the master re-resolves a held key's repeats with the live
    // modifiers and group (e, then E once Shift went down) but replays the down's keysym on the up, so one key can go
    // in as several keysyms, and a down on a keycode another keysym holds (Print and Sys_Req) takes that key over
    private readonly Dictionary<ulong, Press> _presses = [];
    private readonly HashSet<ulong> _upOwed = [];        // keysyms let go with their press, whose own up is swallowed
    private Press? _lastPress;                           // the latest non-modifier press still held: a repeat's key
    private readonly Lock _lock = new();                 // protects the temp-binding state above and the natural holds
    private readonly ILogger<XorgOutputHandler> _log;
    private readonly ShiftedKeypad _shiftedKeypad = new();
    private readonly ScrollAccumulator _scrollNotches = new();
    private readonly NumLockRestore _numLockRestore = new();
    private int _keysymsPerKeycode = 4;  // actual server value; overridden in FindUnusedKeycodes
    private ulong[] _emptyCharSlots = [0UL, 0UL, 0UL, 0UL];  // pre-allocated; sized after FindUnusedKeycodes

    public XorgOutputHandler(ILogger<XorgOutputHandler> log)
    {
        _log = log;
        _display = XlibRuntime.OpenDisplayOrThrow("input injection");

        _screen = NativeMethods.XDefaultScreen(_display);
        _rootWindow = NativeMethods.XDefaultRootWindow(_display);
        // allow XTest events during active grabs (e.g. fullscreen games)
        _ = NativeMethods.XTestGrabControl(_display, true);
        FindUnusedKeycodes();
        // loads Xlib's xkb keymap copy now, which selects the map notifies DrainKeymapChanges reads
        _ = NativeMethods.XkbKeycodeToKeysym(_display, (uint)_minKeycode, 0, 0);
    }

    private unsafe void FindUnusedKeycodes()
    {
        _ = NativeMethods.XDisplayKeycodes(_display, out var minKeycode, out var maxKeycode);
        _minKeycode = minKeycode;
        _maxKeycode = maxKeycode;
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
            if (!allEmpty) continue;
            _unusedKeycodes.Enqueue(minKeycode + i);
            _ = _spareKeycodes.Add((uint)(minKeycode + i));
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

    // the lock state our own lock taps left, so rapid events don't see stale XkbGetState results. Trusted only for
    // LockTapSettleMs after the last tap: past that the server's state is the truth, and catches the slave's own
    // keyboard changing a lock between visits, which a restore must not undo
    private byte? _pendingLockedMods;
    private long _lastLockTapTick;
    private const long LockTapSettleMs = 500;

    internal Func<long> TickCount { get; init; } = static () => Environment.TickCount64;

    public void InjectKey(KeyEventMessage msg)
    {
        var isDown = msg.Type == KeyEventType.KeyDown;
        if (msg.Key is { } keypadKey) msg = msg with { Key = _shiftedKeypad.Injected(keypadKey, msg.Type, msg.Modifiers) };
        if (isDown && msg.Key == SpecialKey.NumLock) _numLockRestore.Toggled();

        // sync CapsLock/NumLock state before injecting (skip when injecting the lock keys themselves)
        if (isDown && msg.Key is not (SpecialKey.CapsLock or SpecialKey.NumLock))
            SyncLockState(msg.Modifiers, msg.Key is { } k ? XorgKeyResolver.KeypadNumLock(k, (msg.Modifiers & KeyModifiers.Shift) != 0) : null);

        if (msg.Character is { } ch)
        {
            // unicode char → keysym, injected on its natural keycode when the layout has it there, else temp-bound
            var keysym = XorgKeysym.Of(ch);
            InjectCharKeysym(keysym, isDown, msg.Modifiers, msg.IsRepeat);
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
            // a key between Super's press and release, so GNOME and KDE take it as used and open no overview for it
            if (!isDown && msg.Chorded == true && key is SpecialKey.Super_L or SpecialKey.Super_R)
            {
                InjectKeysym(XorgVirtualKey.VoidSymbol, true);
                InjectKeysym(XorgVirtualKey.VoidSymbol, false);
            }
            var keysym = XorgKeysym.Injected(key);
            if (keysym != 0)
                InjectKeysym(keysym, isDown, msg.IsRepeat);
            // invalidate tracked state so next sync re-queries from server
            if (key is SpecialKey.CapsLock or SpecialKey.NumLock)
                _pendingLockedMods = null;
        }
    }

    public void InjectMouseButton(MouseButtonMessage msg)
    {
        var button = XorgDisplayHelper.X11Button(msg.Button);
        if (button == 0) return;

        _ = NativeMethods.XTestFakeButtonEvent(_display, button, msg.IsPressed, 0);
        _ = NativeMethods.XFlush(_display);
    }

    public void InjectMouseScroll(MouseScrollMessage msg)
    {
        // x11 scroll: buttons 4=up, 5=down, 6=left, 7=right, a press per notch
        var notches = _scrollNotches.Take(msg.XDelta, msg.YDelta);
        InjectScrollAxis(4u, 5u, notches.Y);
        InjectScrollAxis(7u, 6u, notches.X);
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
    // compatibility. on key-down, records the press so its key-up uses the same keycode even if
    // the XKB layout changes between press and release.
    private void InjectCharKeysym(ulong keysym, bool isDown, KeyModifiers modifiers, bool isRepeat)
    {
        lock (_lock)
        {
            // a repeat or the key-up goes on the keycode the down took, not re-resolved: the layout may have changed
            if (PressHeld(keysym, isDown)) return;

            var activeGroup = DrainKeymapChanges();
            var naturalKeycode = NativeMethods.XKeysymToKeycode(_display, keysym);

            // skip the natural keycode path if the keysym is held on a temp keycode: key-up must release that one.
            // AltGr guard is key-down only: on key-up the _naturalHeld check above already handled it.
            if (naturalKeycode != 0 && !_tempHeld.Contains(keysym) &&
                (!isDown || (modifiers & KeyModifiers.AltGr) == 0))
            {
                // the effective XKB group (layout), so multi-group setups (e.g. Cyrillic+Latin switcher) check
                // whether the keysym lives on the natural keycode under the group that will type it
                if (_spareKeycodes.Contains(naturalKeycode)) naturalKeycode = NaturalKeycode(keysym, activeGroup);
                if (naturalKeycode == 0)
                {
                    TempPress(keysym, isDown, isRepeat);
                    return;
                }

                // level 0: unshifted base — safe regardless of held modifiers
                if (NativeMethods.XkbKeycodeToKeysym(_display, naturalKeycode, activeGroup, 0) == keysym)
                {
                    PressNatural(keysym, naturalKeycode, isDown, isRepeat);
                    return;
                }

                // level 1 (Shift variant): on key-down require Shift to be held.
                // CapsLock is intentionally excluded: slave CapsLock state may not match master's at startup,
                // so chars uppercase-via-CapsLock fall through to temp-binding (which is CapsLock-independent).
                if (NativeMethods.XkbKeycodeToKeysym(_display, naturalKeycode, activeGroup, 1) == keysym &&
                    (!isDown || (modifiers & KeyModifiers.Shift) != 0))
                {
                    PressNatural(keysym, naturalKeycode, isDown, isRepeat);
                    return;
                }
            }

            // keysym not at level 0/1 of natural keycode — temp-bind at all levels so modifier state doesn't matter
            TempPress(keysym, isDown, isRepeat);
        }
    }

    // a special key goes down on its keycode under the active group, recorded so its key-up and ReleaseHeld let go of
    // that keycode even if the layout or group changed while it was held. under the lock: the record is read and
    // written with _tempHeld, which decides between the two paths, and an uncontended lock costs nothing beside the
    // XkbGetState round trip the group lookup takes
    private void InjectKeysym(ulong keysym, bool isDown, bool isRepeat = false)
    {
        lock (_lock)
        {
            if (PressHeld(keysym, isDown)) return;
            if (_tempHeld.Contains(keysym))
            {
                TempPress(keysym, isDown, isRepeat);
                return;
            }

            var group = DrainKeymapChanges();
            var keycode = NaturalSpecialKeycode(keysym, group);
            // a modifier on a spare keycode modifies nothing, being in no modifier map: its own key in another group
            // can act as that key's modifier in the active group (us,de under de: Alt_R on RALT is AltGr), accepted
            if (keycode == 0 && XorgKeyResolver.ModifierBit(keysym) != 0) keycode = OtherGroupKeycode(keysym, group);
            if (keycode == 0)
            {
                TempPress(keysym, isDown, isRepeat);
                return;
            }
            PressNatural(keysym, keycode, isDown, isRepeat);
        }
    }

    // a down of a keysym already held repeats on the keycode it went down on; an up of any keysym a press went in as
    // lets that whole press go. false when the keysym is not held
    private bool PressHeld(ulong keysym, bool isDown)
    {
        if (!isDown) return Release(keysym);
        if (!_naturalHeld.TryGetValue(keysym, out var keycode)) return false;
        _ = NativeMethods.XTestFakeKeyEvent(_display, keycode, true, 0);
        _ = NativeMethods.XFlush(_display);
        return true;
    }

    // a keysym not held goes down on the keycode and holds it. a key-up with no down of ours still goes out, as
    // before, since the key may be stuck from before this run, unless a held keysym has that keycode
    private void PressNatural(ulong keysym, uint keycode, bool isDown, bool isRepeat)
    {
        if (isDown) Hold(keysym, keycode, isRepeat);
        else if (_naturalHeld.ContainsValue(keycode)) return;
        _ = NativeMethods.XTestFakeKeyEvent(_display, keycode, isDown, 0);
        _ = NativeMethods.XFlush(_display);
    }

    private void TempPress(ulong keysym, bool isDown, bool isRepeat)
    {
        TempBind(keysym, isDown);
        if (isDown && _tempHeld.Contains(keysym)) Hold(keysym, null, isRepeat);
    }

    // a down that went out joins the press holding its keycode (Print and Sys_Req share one; e then E, a repeat the
    // master re-resolved under Shift), else, as a repeat, the last key pressed, which is the one the OS repeats
    private void Hold(ulong keysym, uint? keycode, bool isRepeat)
    {
        _ = _upOwed.Remove(keysym);
        if (keycode is { } natural) _naturalHeld[keysym] = natural;
        if (_presses.ContainsKey(keysym)) return;
        var press = HolderOf(keysym, keycode) ?? (isRepeat ? _lastPress : null) ?? new Press();
        _ = press.Keysyms.Add(keysym);
        _presses[keysym] = press;
        if (!isRepeat && XorgKeyResolver.ModifierBit(keysym) == 0) _lastPress = press;
    }

    private Press? HolderOf(ulong keysym, uint? keycode)
    {
        if (keycode is not { } natural) return null;
        foreach (var (held, heldKeycode) in _naturalHeld)
        {
            if (held != keysym && heldKeycode == natural) return _presses[held];
        }
        return null;
    }

    // the first up from any keysym of a press lets each of its keycodes go once. the master sends only the down's up
    // for a re-resolved repeat, but both of two keys sharing a keycode: the other's up, owed, sends nothing
    private bool Release(ulong keysym)
    {
        if (_upOwed.Remove(keysym)) return true;
        if (!_presses.TryGetValue(keysym, out var press)) return false;
        if (press == _lastPress) _lastPress = null;
        var keycodes = new HashSet<uint>();
        foreach (var held in press.Keysyms)
        {
            _ = _presses.Remove(held);
            if (held != keysym) _ = _upOwed.Add(held);
            if (_naturalHeld.Remove(held, out var natural)) _ = keycodes.Add(natural);
            if (_tempHeld.Remove(held) && _tempBound.TryGetValue(held, out var bound)) _ = keycodes.Add((uint)bound.Keycode);
        }
        foreach (var keycode in keycodes)
            _ = NativeMethods.XTestFakeKeyEvent(_display, keycode, false, 0);
        _ = NativeMethods.XFlush(_display);
        return true;
    }

    // the keysyms one key of the master's went in as
    private sealed class Press
    {
        public HashSet<ulong> Keysyms { get; } = [];
    }

    // Xlib's keymap copy refreshes only once this connection reads the server's map notify, and the display selects no
    // events of its own, so nothing else reads them. Xlib's xkb support selects map and new-keyboard notifies when it
    // loads the copy (forced in the constructor), and marks the copy stale as it reads either off the socket, for the
    // next Xkb lookup to refetch (_XkbCheckPendingRefresh): so no XRefreshKeyboardMapping (the core table it also
    // refreshes is one we never read), only the queue drained so it does not grow. the group query is a round trip,
    // so every notify the server sent before it, an outside setxkbmap's included, is read by the time it returns. our
    // own temp binds notify too, harmlessly. returns the active group
    private int DrainKeymapChanges()
    {
        var group = NativeMethods.XkbGetState(_display, NativeMethods.XkbUseCoreKbd, out var xkbState) == 0 ? xkbState.Group : 0;
        while (NativeMethods.XEventsQueued(_display, NativeMethods.QueuedAlready) > 0)
            _ = NativeMethods.XNextEvent(_display, out _);
        return group;
    }

    // XKeysymToKeycode searches level 0 of every keycode before level 1 of any, so a keysym bound on a spare keycode
    // (at every level) is found there before on its own key when that has it at level 1, or in another group. these
    // search again with the spare keycodes left out: the keysym's keycode at level 0, else level 1, of the group, or 0.
    // only for a keysym that has a temp binding, and in Xlib's cached keymap, so a few hundred lookups with no round trip
    private uint NaturalKeycode(ulong keysym, int group)
    {
        for (var level = 0; level <= 1; level++)
        {
            for (var keycode = (uint)_minKeycode; keycode <= (uint)_maxKeycode; keycode++)
            {
                if (!_spareKeycodes.Contains(keycode) && NativeMethods.XkbKeycodeToKeysym(_display, keycode, group, level) == keysym)
                    return keycode;
            }
        }
        return 0;
    }

    // a special key goes down where the active group has it, never another group's key: that one types something else
    // under the active group. a keysym only another group has is temp-bound instead, as one the layout lacks
    // a keypad keysym only another group has (KP_Decimal on us,ru under ru) is temp-bound too, a non-numpad key to a
    // game reading keycodes: accepted, since the other group's key would type a different character
    private uint NaturalSpecialKeycode(ulong keysym, int group)
    {
        // XKeysymToKeycode's pick when it types the keysym under the group, as before; else the scan for one that does
        var first = NativeMethods.XKeysymToKeycode(_display, keysym);
        if (first == 0) return 0;
        if (!_spareKeycodes.Contains(first) && (EffectiveKeysym(first, group, 0) == keysym || EffectiveKeysym(first, group, 1) == keysym))
            return first;
        for (var level = 0; level <= 1; level++)
        {
            for (var keycode = (uint)_minKeycode; keycode <= (uint)_maxKeycode; keycode++)
            {
                if (!_spareKeycodes.Contains(keycode) && EffectiveKeysym(keycode, group, level) == keysym)
                    return keycode;
            }
        }
        return 0;
    }

    // XkbKeycodeToKeysym gives NoSymbol past a key's last group, but the server wraps the group into the key's range
    // (Shift_L has one group and still types Shift_L under the second layout). wrap as the default action does
    private ulong EffectiveKeysym(uint keycode, int group, int level)
    {
        var keysym = NativeMethods.XkbKeycodeToKeysym(_display, keycode, group, level);
        if (keysym != 0 || group == 0) return keysym;
        var groups = MaxGroups;
        while (groups > 0 && NativeMethods.XkbKeycodeToKeysym(_display, keycode, groups - 1, 0) == 0 &&
               NativeMethods.XkbKeycodeToKeysym(_display, keycode, groups - 1, 1) == 0)
            groups--;
        return groups == 0 || group < groups ? 0 : NativeMethods.XkbKeycodeToKeysym(_display, keycode, group % groups, level);
    }

    private const int MaxGroups = 4;  // XkbNumKbdGroups

    // the keysym's own key in another group, which the active group types something else on
    private uint OtherGroupKeycode(ulong keysym, int activeGroup)
    {
        for (var group = 0; group < MaxGroups; group++)
        {
            if (group != activeGroup && NaturalKeycode(keysym, group) is not 0 and var keycode) return keycode;
        }
        return 0;
    }

    // types a keysym the layout lacks on a spare keycode, bound with the keysym at all levels so modifier state on the
    // slave doesn't affect the result. the binding stays after key-up: a client may fetch the keymap only when it
    // handles the mapping notify, after the key event, and a key it finds unbound by then reaches the app with no
    // keysym. so a keysym bound before is pressed again on its keycode with no remap, and a new one takes an unused
    // spare, else the least recently used binding not held down. never a held one: its key-up must release the keysym
    // it went down as
    private void TempBind(ulong keysym, bool isDown)
    {
        if (!isDown)
        {
            if (!_tempHeld.Remove(keysym) || !_tempBound.TryGetValue(keysym, out var held)) return;
            _ = NativeMethods.XTestFakeKeyEvent(_display, (uint)held.Keycode, false, 0);
            _ = NativeMethods.XFlush(_display);
            return;
        }

        if (_tempBound.TryGetValue(keysym, out var bound))
        {
            // a layout change replaces the server's keymap, our bindings with it
            if (ServerKeysym(bound.Keycode) != keysym) Bind(bound.Keycode, keysym);
        }
        else if (FreeKeycode() is { } free)
        {
            Bind(free, keysym);
            bound = (free, 0);
        }
        else
        {
            _log.LogWarning("No spare keycode free (every one held down) — dropping keysym 0x{Keysym:X}", keysym);
            return;
        }
        _tempBound[keysym] = (bound.Keycode, ++_tempUses);
        _ = _tempHeld.Add(keysym);
        _ = NativeMethods.XTestFakeKeyEvent(_display, (uint)bound.Keycode, true, 0);
        _ = NativeMethods.XFlush(_display);
    }

    // an unused spare keycode, else the one of the least recently used binding not held down, now unbound from it
    private int? FreeKeycode()
    {
        if (_unusedKeycodes.TryDequeue(out var unused)) return unused;
        ulong? lru = null;
        var oldest = long.MaxValue;
        foreach (var (keysym, (_, lastUse)) in _tempBound)
        {
            if (lastUse >= oldest || _tempHeld.Contains(keysym)) continue;
            lru = keysym;
            oldest = lastUse;
        }
        return lru is { } evicted && _tempBound.Remove(evicted, out var binding) ? binding.Keycode : null;
    }

    // synced, so the keysym is on the server before the key event that needs it
    private void Bind(int keycode, ulong keysym)
    {
        var filledSlots = new ulong[_keysymsPerKeycode];
        Array.Fill(filledSlots, keysym);
        _ = NativeMethods.XChangeKeyboardMapping(_display, keycode, _keysymsPerKeycode, filledSlots, 1);
        _ = NativeMethods.XSync(_display, false);
        Remaps++;
    }

    // the XChangeKeyboardMapping calls made to bind a keysym, and how many spare keycodes there are, for tests
    internal int Remaps { get; private set; }
    internal int SpareKeycodes => _spareKeycodes.Count;

    // asks the server: Xlib's cached keymap need not have caught up with a layout change yet
    private unsafe ulong ServerKeysym(int keycode)
    {
        var map = NativeMethods.XGetKeyboardMapping(_display, (uint)keycode, 1, out _);
        if (map == nint.Zero) return 0;
        var keysym = *(ulong*)map;
        _ = NativeMethods.XFree(map);
        return keysym;
    }

    // lets go of every key still held. temp bindings stay: a client may yet fetch the keymap for a key just released
    private void ReleaseHeld()
    {
        foreach (var keycode in _naturalHeld.Values.Concat(_tempHeld.Select(keysym => (uint)_tempBound[keysym].Keycode)).Distinct())
            _ = NativeMethods.XTestFakeKeyEvent(_display, keycode, false, 0);
        _naturalHeld.Clear();
        _tempHeld.Clear();
        _presses.Clear();
        _upOwed.Clear();
        _lastPress = null;
        _ = NativeMethods.XFlush(_display);
    }

    // numLock: what the key about to be injected needs, or null to leave NumLock alone
    private void SyncLockState(KeyModifiers mods, bool? numLock)
    {
        if (LockedMods() is not { } lockedMods) return;
        SyncLockKey(XorgVirtualKey.CapsLock, NativeMethods.LockMask, (mods & KeyModifiers.CapsLock) != 0, ref lockedMods);
        if (numLock is { } wantNumLock && SyncLockKey(XorgVirtualKey.NumLock, NativeMethods.Mod2Mask, wantNumLock, ref lockedMods))
            _numLockRestore.Synced(had: !wantNumLock);
        _pendingLockedMods = lockedMods;
    }

    public void EndVisit()
    {
        _scrollNotches.Reset();
        // the relay sent a key-up for every key it saw held; this catches any whose key-up never came
        lock (_lock) ReleaseHeld();
        if (_numLockRestore.Take() is not { } before || LockedMods() is not { } lockedMods) return;
        SyncLockKey(XorgVirtualKey.NumLock, NativeMethods.Mod2Mask, before, ref lockedMods);
        _pendingLockedMods = lockedMods;
    }

    private byte? LockedMods()
    {
        if (_pendingLockedMods is { } pending && TickCount() - _lastLockTapTick < LockTapSettleMs) return pending;
        return NativeMethods.XkbGetState(_display, NativeMethods.XkbUseCoreKbd, out var xkbState) == 0 ? xkbState.LockedMods : null;
    }

    // returns whether it flipped the lock
    private bool SyncLockKey(ulong keysym, uint lockMask, bool want, ref byte lockedMods)
    {
        var have = (lockedMods & lockMask) != 0;
        if (have == want) return false;
        var keycode = NativeMethods.XKeysymToKeycode(_display, keysym);
        if (keycode == 0) return false;
        _ = NativeMethods.XTestFakeKeyEvent(_display, keycode, true, 0);
        _ = NativeMethods.XTestFakeKeyEvent(_display, keycode, false, 0);
        _ = NativeMethods.XFlush(_display);
        _lastLockTapTick = TickCount();
        lockedMods = want ? (byte)(lockedMods | lockMask) : (byte)(lockedMods & ~lockMask);
        return true;
    }

    public ScreenPoint? GetCursorPosition() => XorgDisplayHelper.CursorPosition(_display, _rootWindow);

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
                // the keyboard is left as found: nothing held, every spare keycode empty again
                ReleaseHeld();
                foreach (var (keycode, _) in _tempBound.Values)
                    _ = NativeMethods.XChangeKeyboardMapping(_display, keycode, _keysymsPerKeycode, _emptyCharSlots, 1);
                _tempBound.Clear();
                _ = NativeMethods.XSync(_display, false);
            }
            if (_cursorHidden)
                NativeMethods.XFixesShowCursor(_display, _rootWindow);
            _ = NativeMethods.XCloseDisplay(_display);
        }
    }
}
