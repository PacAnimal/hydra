using Hydra.Keyboard;

namespace Hydra.Platform.Windows;

// translates low-level keyboard hook data into KeyEvents using ToUnicodeEx.
// follows the same server-is-master approach as the other resolvers: resolve
// the final character/key here using our own keyboard layout, so the receiver
// only needs to inject what it's told.
internal sealed class WinKeyResolver
{
    // full key state array (256 bytes): high bit set = pressed, low bit = toggled (for lock keys)
    private readonly byte[] _keyState = new byte[256];

    // scratch buffer for ToUnicodeEx — avoid allocation on each keypress
    private readonly byte[] _resolveState = new byte[256];

    private readonly Dictionary<int, CharClassification> _keyDownId = [];

    // synthetic tracking key for KP_Enter: VK_RETURN (0x0D) is shared with regular Return.
    // using a distinct key lets both be held simultaneously without overwriting each other.
    private const int KpEnterTrackingVk = WinVirtualKey.Return | 0x10000;

    // pending dead key combining character (e.g. '\u0301' for acute accent)
    private char _pendingDeadKey;
    private char _pendingDeadSpacing;  // spacing form used when composition fails (e.g. dead_circumflex + space → ^)
    private int _pendingDeadKeyVk;     // VK that produced the pending dead key; used to detect auto-repeat vs. a second dead key

    // AltGr detection: deferred LCtrl (may be synthetic from AltGr — decided when RMenu arrives)
    private (int Vk, KeyModifiers Mods, uint Time)? _deferredLCtrl;
    // set when LLKHF_INJECTED caught a synthetic LCtrl (alternative detection path for drivers that do set it)
    private bool _injectedLCtrlPending;

    internal KeyEvent[]? Resolve(int wParam, KBDLLHOOKSTRUCT info)
    {
        var vk = (int)info.vkCode;
        var isKeyUp = wParam is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;

        // update tracked key state before resolving
        UpdateKeyState(vk, info.flags, isKeyUp);

        // suppress injected modifier events — state is tracked in UpdateKeyState but event is not forwarded.
        // also detects the synthetic LCtrl that AltGr generates when LLKHF_INJECTED IS set.
        if ((info.flags & NativeMethods.LLKHF_INJECTED) != 0 && IsModifier(vk)) return null;

        var mods = GetModifiers();

        if (isKeyUp)
        {
            _injectedLCtrlPending = false;
            var prefix = FlushDeferredLCtrl();
            // KP_Enter key-up: use synthetic tracking key so it doesn't collide with Return
            var trackingVk = (vk == WinVirtualKey.Return && (info.flags & NativeMethods.LLKHF_EXTENDED) != 0)
                ? KpEnterTrackingVk : vk;
            var up = KeyResolver.ReplayKeyUp(_keyDownId, trackingVk, mods);
            return Events(prefix, up);
        }

        // AltGr detection: RMenu preceded by a same-timestamp or LLKHF_INJECTED synthetic LCtrl
        if (vk == WinVirtualKey.RMenu)
        {
            var isAltGr = (_deferredLCtrl is { Time: var t } && t == info.time) || _injectedLCtrlPending;
            _injectedLCtrlPending = false;
            if (isAltGr)
            {
                _deferredLCtrl = null;
                // clear synthetic LCtrl from key state so it doesn't bleed into future GetModifiers() calls
                _keyState[WinVirtualKey.LControl] = 0;
                _keyState[WinVirtualKey.Control] = (byte)((_keyState[WinVirtualKey.RControl] & 0x80) != 0 ? 0x80 : 0);
                var altGrMods = GetModifiers();
                // flush pending dead key before AltGr: dead_acute + AltGr+e → '´' then AltGr char
                var deadFlush = FlushPendingDeadKey();
                _keyDownId[vk] = new CharClassification(null, SpecialKey.AltGr);
                var altGrEvent = KeyEvent.Special(KeyEventType.KeyDown, SpecialKey.AltGr, altGrMods);
                return deadFlush is not null ? [.. deadFlush, altGrEvent] : [altGrEvent];
            }
        }
        else
        {
            _injectedLCtrlPending = false;
        }

        // suppress auto-repeat of a deferred LCtrl: same VK while _deferredLCtrl is pending means the key
        // is still held and the OS is repeating it. flushing here would prematurely emit Control_L before
        // we know whether the next key is RMenu (AltGr) — so drop the repeat silently.
        if (_deferredLCtrl?.Vk == vk) return null;

        // flush deferred LCtrl — it was not followed by same-timestamp RMenu, so it's a real Ctrl press
        var flushed = FlushDeferredLCtrl();

        // numpad digits: only delivered when NumLock is on (NumLock off gives navigation VKs instead).
        // emit as KP_0-KP_9 SpecialKey events so the slave injects the physical numpad key.
        if (vk is >= WinVirtualKey.Numpad0 and <= WinVirtualKey.Numpad9)
        {
            if (_keyDownId.ContainsKey(vk)) return Events(flushed, null);
            var kpKey = (SpecialKey)((uint)SpecialKey.KP_0 + (vk - WinVirtualKey.Numpad0));
            _keyDownId[vk] = new CharClassification(null, kpKey);
            return Combine(flushed, FlushPendingDeadKey(), KeyEvent.Special(KeyEventType.KeyDown, kpKey, mods));
        }
        // numpad decimal/separator: use ToUnicodeEx for locale-correct char ('.' on US, ',' on European layouts)
        if (vk == WinVirtualKey.Decimal)
        {
            if (_keyDownId.ContainsKey(vk)) return Events(flushed, null);
            var decEvent = ResolveCharacter(vk, info.scanCode, info.flags, mods);
            // always store (even sentinel) so auto-repeat is suppressed on subsequent events
            _keyDownId[vk] = decEvent is not null
                ? new CharClassification(decEvent[0].Character, decEvent[0].Key)
                : new CharClassification(null, null);
            return Combine(flushed, FlushPendingDeadKey(), decEvent);
        }

        // numpad Enter: VK_RETURN with extended-key flag — distinct from regular Enter
        if (vk == WinVirtualKey.Return && (info.flags & NativeMethods.LLKHF_EXTENDED) != 0)
        {
            if (_keyDownId.ContainsKey(KpEnterTrackingVk)) return Events(flushed, null);
            _keyDownId[KpEnterTrackingVk] = new CharClassification(null, SpecialKey.KP_Enter);
            return Combine(flushed, FlushPendingDeadKey(), KeyEvent.Special(KeyEventType.KeyDown, SpecialKey.KP_Enter, mods));
        }

        if (WinSpecialKeyMap.Instance.TryGet((ulong)vk, out var specialKey))
        {
            // suppress auto-repeat — only emit on initial press, not while held.
            // key type check guards against cross-type collision (e.g. KP_Enter vs Return sharing VK_RETURN).
            // note: KP_Enter hits the vk==Return && LLKHF_EXTENDED branch above before reaching here,
            // so the tracked.Key == specialKey guard never needs a KP_Enter carve-out.
            if (_keyDownId.TryGetValue(vk, out var tracked) && tracked.Key == specialKey) return Events(flushed, null);
            // modifier keys are transparent to dead key composition (Shift while dead key pending stays armed).
            // non-modifier special keys (Tab, Escape, arrows, F-keys, etc.) abort composition: flush spacing form.
            var flushedDead = specialKey.IsModifier() ? null : FlushPendingDeadKey();

            if (specialKey == SpecialKey.Control_L)
            {
                // defer LCtrl — may be AltGr's synthetic key; decided on next RMenu event
                _deferredLCtrl ??= (vk, mods, info.time);
                return Combine(flushed, flushedDead, (KeyEvent?)null);
            }

            _keyDownId[vk] = new CharClassification(null, specialKey);
            return Combine(flushed, flushedDead, KeyEvent.Special(KeyEventType.KeyDown, specialKey, mods));
        }

        // suppress character key auto-repeat
        if (_keyDownId.ContainsKey(vk)) return Events(flushed, null);

        // dead key pending + Ctrl/Super shortcut: flush spacing form first, then send the shortcut.
        // without this, dead_grave + Ctrl+A would compose to 'à' with Ctrl — wrong on every slave.
        // mirrors Mac (MacKeyResolver) and Linux (XorgKeyResolver) shortcut-flush behaviour.
        // AltGr is excluded: its dead-key flush happens earlier when AltGr itself is pressed.
        var shortcutDeadFlush = ((mods & (KeyModifiers.Control | KeyModifiers.Super)) != 0)
            ? FlushPendingDeadKey() : null;

        var charEvent = ResolveCharacter(vk, info.scanCode, info.flags, mods);
        return Combine(flushed, shortcutDeadFlush, charEvent);
    }

    private void UpdateKeyState(int vk, uint flags, bool isKeyUp)
    {
        // injected LCtrl is AltGr's synthetic key: don't track in _keyState (it's not a real Ctrl press),
        // but record the pending flag so RMenu detection works.
        // ALL other injected modifiers (e.g. Shift from accessibility/IME software) DO update _keyState
        // so that GetModifiers() and ToUnicodeEx stay accurate for subsequent character resolution.
        bool injected = (flags & NativeMethods.LLKHF_INJECTED) != 0;
        if (injected && IsModifier(vk))
        {
            if (vk == WinVirtualKey.LControl && !isKeyUp)
                _injectedLCtrlPending = true;
            if (vk == WinVirtualKey.LControl)
                return;  // AltGr synthetic only — other injected modifiers fall through to state tracking
        }

        if (isKeyUp)
        {
            _keyState[vk] = 0;
        }
        else
        {
            _keyState[vk] = 0x80;

            // toggle lock keys on keydown; sync from OS to avoid startup state mismatch
            if (vk == WinVirtualKey.Capital || vk == WinVirtualKey.Numlock || vk == WinVirtualKey.Scroll)
                _keyState[vk] = (byte)(0x80 | (NativeMethods.GetKeyState(vk) & 0x01));
        }

        // sync generic modifier VKs (ToUnicodeEx checks VK_SHIFT, not VK_LSHIFT)
        _keyState[WinVirtualKey.Shift] = (byte)((_keyState[WinVirtualKey.LShift] | _keyState[WinVirtualKey.RShift]) != 0 ? 0x80 : 0);
        _keyState[WinVirtualKey.Control] = (byte)((_keyState[WinVirtualKey.LControl] | _keyState[WinVirtualKey.RControl]) != 0 ? 0x80 : 0);
        _keyState[WinVirtualKey.Menu] = (byte)((_keyState[WinVirtualKey.LMenu] | _keyState[WinVirtualKey.RMenu]) != 0 ? 0x80 : 0);
    }

    private KeyModifiers GetModifiers()
    {
        var mods = KeyModifiers.None;
        if ((_keyState[WinVirtualKey.LShift] | _keyState[WinVirtualKey.RShift]) != 0) mods |= KeyModifiers.Shift;
        if ((_keyState[WinVirtualKey.LControl] | _keyState[WinVirtualKey.RControl]) != 0) mods |= KeyModifiers.Control;
        if ((_keyState[WinVirtualKey.LMenu] | _keyState[WinVirtualKey.RMenu]) != 0) mods |= KeyModifiers.Alt;
        if ((_keyState[WinVirtualKey.LWin] | _keyState[WinVirtualKey.RWin]) != 0) mods |= KeyModifiers.Super;
        if ((_keyState[WinVirtualKey.Capital] & 0x01) != 0) mods |= KeyModifiers.CapsLock;
        if ((_keyState[WinVirtualKey.Numlock] & 0x01) != 0) mods |= KeyModifiers.NumLock;
        if ((_keyState[WinVirtualKey.Scroll] & 0x01) != 0) mods |= KeyModifiers.ScrollLock;
        // AltGr = RMenu alone; the synthesized LCtrl is detected and stripped separately
        if ((_keyState[WinVirtualKey.RMenu] & 0x80) != 0) mods |= KeyModifiers.AltGr;
        // strip Control and Alt when AltGr is active — receiver only needs AltGr (matches Linux behaviour)
        if ((mods & KeyModifiers.AltGr) != 0)
            mods &= ~(KeyModifiers.Control | KeyModifiers.Alt);
        return mods;
    }

    private KeyEvent[]? ResolveCharacter(int vk, uint scanCode, uint hookFlags, KeyModifiers mods)
    {
        Array.Copy(_keyState, _resolveState, 256);

        // AltGr: pressing the AltGr key synthesizes an injected LCtrl, which UpdateKeyState strips
        // to avoid phantom Ctrl in modifier reporting. Restore it here so ToUnicodeEx sees the
        // full Ctrl+RMenu state it needs to resolve the AltGr character layer.
        bool altGrActive = (_resolveState[WinVirtualKey.RMenu] & 0x80) != 0;
        if (altGrActive)
        {
            _resolveState[WinVirtualKey.LControl] = 0x80;
            _resolveState[WinVirtualKey.Control] = 0x80;
        }
        else
        {
            // strip ctrl so ToUnicodeEx produces letters, not control codes
            _resolveState[WinVirtualKey.LControl] = 0;
            _resolveState[WinVirtualKey.RControl] = 0;
            _resolveState[WinVirtualKey.Control] = 0;
            // strip Shift when Ctrl is held — Ctrl+Shift+key should send the base char (same as Win+Shift).
            // without this, Ctrl+Shift+4 on Norwegian sends '¤' (shifted 4) instead of '4'.
            _resolveState[WinVirtualKey.LShift] = 0;
            _resolveState[WinVirtualKey.RShift] = 0;
            _resolveState[WinVirtualKey.Shift] = 0;
        }

        // strip Win and, when Win is held, also Shift — so shortcut keys return their base character
        // (e.g. '4' not '¤' on Norwegian for Win+Shift+4), matching the Linux/Mac resolver behaviour.
        bool superActive = ((_resolveState[WinVirtualKey.LWin] | _resolveState[WinVirtualKey.RWin]) & 0x80) != 0;
        _resolveState[WinVirtualKey.LWin] = 0;
        _resolveState[WinVirtualKey.RWin] = 0;
        if (superActive)
        {
            _resolveState[WinVirtualKey.LShift] = 0;
            _resolveState[WinVirtualKey.RShift] = 0;
            _resolveState[WinVirtualKey.Shift] = 0;
        }

        // get keyboard layout of the foreground window's thread for correct character mapping
        var hkl = GetForegroundKeyboardLayout();

        // pass extended key flag through to ToUnicodeEx (same approach as input-leap MSWindowsKeyState)
        uint uFlags = hookFlags & NativeMethods.LLKHF_EXTENDED;

        int count;
        char rawChar;
        unsafe
        {
            char* buff = stackalloc char[4];
            fixed (byte* pState = _resolveState)
            {
                count = NativeMethods.ToUnicodeEx((uint)vk, scanCode, pState, buff, 4, uFlags, hkl);

                // altGr fallback: if Ctrl+Alt didn't produce anything, retry without them
                if (count == 0 && altGrActive)
                {
                    _resolveState[WinVirtualKey.LControl] = 0;
                    _resolveState[WinVirtualKey.RControl] = 0;
                    _resolveState[WinVirtualKey.Control] = 0;
                    _resolveState[WinVirtualKey.LMenu] = 0;
                    _resolveState[WinVirtualKey.RMenu] = 0;
                    _resolveState[WinVirtualKey.Menu] = 0;
                    count = NativeMethods.ToUnicodeEx((uint)vk, scanCode, pState, buff, 4, uFlags, hkl);
                }

                if (count == 0) return null;

                // count == -1: dead key — buff[0] has the standalone dead character.
                // flush the system dead key state with a space call so subsequent ToUnicodeEx
                // calls start from a clean state (we track dead keys ourselves).
                if (count < 0)
                {
                    char flush;
                    _ = NativeMethods.ToUnicodeEx(NativeMethods.VK_SPACE, 0, pState, &flush, 1, 0, hkl);
                    var spacingForm = buff[0];

                    // if a different VK produced the current pending dead key, flush it now
                    // (two consecutive dead keys: dead_acute then dead_grave → emit '´' then record new).
                    // same VK = auto-repeat: don't flush (just re-record the same pending state).
                    var prevDeadFlush = (_pendingDeadKey != '\0' && _pendingDeadKeyVk != vk)
                        ? FlushPendingDeadKey() : null;

                    if (KeyResolver.SpacingToCombining.TryGetValue(spacingForm, out var combining))
                    {
                        // standard dead key: spacing form → combining char (e.g. ´ → U+0301)
                        _pendingDeadKey = combining;
                        _pendingDeadSpacing = spacingForm;
                    }
                    else if (spacingForm is >= '\u0300' and <= '\u036F')
                    {
                        // some layouts (e.g. polytonic Greek) return the combining char directly
                        _pendingDeadKey = spacingForm;
                        _pendingDeadSpacing = '\0';
                    }
                    _pendingDeadKeyVk = vk;
                    return prevDeadFlush;
                }

                rawChar = buff[0];
            }
        }

        var classified = KeyResolver.ClassifyChar(rawChar);
        if (!classified.Ch.HasValue && !classified.Key.HasValue) return null;

        // apply pending dead key composition
        var ch = classified.Ch;
        if (_pendingDeadKey != '\0' && ch.HasValue)
        {
            var dead = _pendingDeadKey;
            var spacing = _pendingDeadSpacing;
            _pendingDeadKey = '\0';
            _pendingDeadSpacing = '\0';
            ch = KeyResolver.ComposeOrSpacing(ch.Value, dead, spacing);
        }

        if (ch.HasValue)
        {
            _keyDownId[vk] = new CharClassification(ch, null);
            return [KeyEvent.Char(KeyEventType.KeyDown, ch.Value, mods)];
        }

        _keyDownId[vk] = new CharClassification(null, classified.Key);
        return [KeyEvent.Special(KeyEventType.KeyDown, classified.Key!.Value, mods)];
    }

    // yields key-up events for every currently held key; called before Reset() on desktop change
    // so the slave can release keys that were physically held when the desktop switched.
    internal IEnumerable<KeyEvent> TakeHeldKeyUps()
    {
        var mods = GetModifiers();
        foreach (var (_, classification) in _keyDownId)
        {
            if (classification.Ch.HasValue)
                yield return KeyEvent.Char(KeyEventType.KeyUp, classification.Ch.Value, mods);
            else if (classification.Key.HasValue)
                yield return KeyEvent.Special(KeyEventType.KeyUp, classification.Key.Value, mods);
            // sentinel (null, null) = dead key placeholder → no event needed
        }
    }

    // resets all resolver state; called on desktop change to prevent phantom modifier bleed
    internal void Reset()
    {
        Array.Clear(_keyState, 0, _keyState.Length);
        _keyDownId.Clear();
        _pendingDeadKey = '\0';
        _pendingDeadSpacing = '\0';
        _pendingDeadKeyVk = 0;
        _deferredLCtrl = null;
        _injectedLCtrlPending = false;
    }

    // flush the deferred LCtrl as a real Ctrl key-down event
    private KeyEvent? FlushDeferredLCtrl()
    {
        if (_deferredLCtrl is not { } def) return null;
        _deferredLCtrl = null;
        _keyDownId[def.Vk] = new CharClassification(null, SpecialKey.Control_L);
        return KeyEvent.Special(KeyEventType.KeyDown, SpecialKey.Control_L, def.Mods);
    }

    // emit a pending dead key as its spacing form before a non-composing key.
    // dead keys with no spacing form (e.g. dead_belowdot) are dropped — never emit the combining char.
    // modifiers are always None: the spacing form belongs to the dead key press, not the aborting key.
    // returns [down, up] pair so the slave does not get a stuck key from the synthetic press.
    private KeyEvent[]? FlushPendingDeadKey()
    {
        if (_pendingDeadKey == '\0') return null;
        var spacing = _pendingDeadSpacing;
        _pendingDeadKey = '\0';
        _pendingDeadSpacing = '\0';
        _pendingDeadKeyVk = 0;
        if (spacing == '\0') return null;  // no spacing form — drop silently
        return [
            KeyEvent.Char(KeyEventType.KeyDown, spacing, KeyModifiers.None),
            KeyEvent.Char(KeyEventType.KeyUp, spacing, KeyModifiers.None),
        ];
    }

    // combine up to two nullable events into an array (returns null when both are null)
    private static KeyEvent[]? Events(KeyEvent? a, KeyEvent? b)
    {
        if (a is null && b is null) return null;
        if (a is null) return [b!];
        if (b is null) return [a];
        return [a, b];
    }

    // combine a single prefix event, a dead-key flush pair, and a single main event
    private static KeyEvent[]? Combine(KeyEvent? a, KeyEvent[]? flush, KeyEvent? c)
    {
        var count = (a is not null ? 1 : 0) + (flush?.Length ?? 0) + (c is not null ? 1 : 0);
        if (count == 0) return null;
        var result = new KeyEvent[count];
        var i = 0;
        if (a is not null) result[i++] = a;
        if (flush is not null) foreach (var e in flush) result[i++] = e;
        if (c is not null) result[i] = c;
        return result;
    }

    // combine a single prefix event, a dead-key flush pair, and an array of main events
    private static KeyEvent[]? Combine(KeyEvent? a, KeyEvent[]? flush, KeyEvent[]? c)
    {
        var count = (a is not null ? 1 : 0) + (flush?.Length ?? 0) + (c?.Length ?? 0);
        if (count == 0) return null;
        var result = new KeyEvent[count];
        var i = 0;
        if (a is not null) result[i++] = a;
        if (flush is not null) foreach (var e in flush) result[i++] = e;
        if (c is not null) foreach (var e in c) result[i++] = e;
        return result;
    }

    // gets the keyboard layout associated with the foreground window's thread.
    // falls back to layout 0 (system default) if there is no foreground window.
    private static nint GetForegroundKeyboardLayout()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == nint.Zero) return NativeMethods.GetKeyboardLayout(0);
        var tid = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
        return NativeMethods.GetKeyboardLayout(tid);
    }

    private static bool IsModifier(int vk) =>
        vk is WinVirtualKey.LShift or WinVirtualKey.RShift
            or WinVirtualKey.LControl or WinVirtualKey.RControl
            or WinVirtualKey.LMenu or WinVirtualKey.RMenu
            or WinVirtualKey.LWin or WinVirtualKey.RWin;
}
