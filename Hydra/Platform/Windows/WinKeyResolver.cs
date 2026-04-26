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

    // pending dead key combining character (e.g. '\u0301' for acute accent)
    private char _pendingDeadKey;
    private char _pendingDeadSpacing;  // spacing form used when composition fails (e.g. dead_circumflex + space → ^)

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
            var up = KeyResolver.ReplayKeyUp(_keyDownId, vk, mods);
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
                _keyDownId[vk] = new CharClassification(null, SpecialKey.AltGr);
                return [KeyEvent.Special(KeyEventType.KeyDown, SpecialKey.AltGr, altGrMods)];
            }
        }
        else
        {
            _injectedLCtrlPending = false;
        }

        // flush deferred LCtrl — it was not followed by same-timestamp RMenu, so it's a real Ctrl press
        var flushed = FlushDeferredLCtrl();

        // numpad digits: only delivered when NumLock is on (NumLock off gives navigation VKs instead).
        // emit as char events so the slave doesn't need to have NumLock active to produce a digit.
        if (vk is >= WinVirtualKey.Numpad0 and <= WinVirtualKey.Numpad9)
        {
            if (_keyDownId.ContainsKey(vk)) return Events(flushed, null);
            _pendingDeadKey = '\0';
            _pendingDeadSpacing = '\0';
            var digit = (char)('0' + (vk - WinVirtualKey.Numpad0));
            _keyDownId[vk] = new CharClassification(digit, null);
            return Events(flushed, KeyEvent.Char(KeyEventType.KeyDown, digit, mods));
        }
        // numpad decimal/separator: use ToUnicodeEx for locale-correct char ('.' on US, ',' on European layouts)
        if (vk == WinVirtualKey.Decimal)
        {
            if (_keyDownId.ContainsKey(vk)) return Events(flushed, null);
            _pendingDeadKey = '\0';
            _pendingDeadSpacing = '\0';
            return Events(flushed, ResolveCharacter(vk, info.scanCode, info.flags, mods));
        }

        if (WinSpecialKeyMap.Instance.TryGet((ulong)vk, out var specialKey))
        {
            // suppress auto-repeat — only emit on initial press, not while held.
            // for deferred LCtrl, _keyDownId won't have it yet, so check _deferredLCtrl too.
            if (_keyDownId.ContainsKey(vk)) return Events(flushed, null);
            _pendingDeadKey = '\0';
            _pendingDeadSpacing = '\0';

            if (specialKey == SpecialKey.Control_L)
            {
                // defer LCtrl — may be AltGr's synthetic key; decided on next RMenu event
                _deferredLCtrl ??= (vk, mods, info.time);
                return Events(flushed, null);
            }

            _keyDownId[vk] = new CharClassification(null, specialKey);
            return Events(flushed, KeyEvent.Special(KeyEventType.KeyDown, specialKey, mods));
        }

        // suppress character key auto-repeat
        if (_keyDownId.ContainsKey(vk)) return Events(flushed, null);

        var charEvent = ResolveCharacter(vk, info.scanCode, info.flags, mods);
        return Events(flushed, charEvent);
    }

    private void UpdateKeyState(int vk, uint flags, bool isKeyUp)
    {
        // ignore injected keys for modifier state (avoids phantom ctrl from AltGr synthesis).
        // track injected LCtrl so we can detect AltGr even when LLKHF_INJECTED is set.
        bool injected = (flags & NativeMethods.LLKHF_INJECTED) != 0;
        if (injected && IsModifier(vk))
        {
            if (vk == WinVirtualKey.LControl && !isKeyUp)
                _injectedLCtrlPending = true;
            return;
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
        // AltGr = RMenu alone; the synthesized LCtrl is detected and stripped separately
        if ((_keyState[WinVirtualKey.RMenu] & 0x80) != 0) mods |= KeyModifiers.AltGr;
        // strip Control and Alt when AltGr is active — receiver only needs AltGr (matches Linux behaviour)
        if ((mods & KeyModifiers.AltGr) != 0)
            mods &= ~(KeyModifiers.Control | KeyModifiers.Alt);
        return mods;
    }

    private KeyEvent? ResolveCharacter(int vk, uint scanCode, uint hookFlags, KeyModifiers mods)
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
                    return null;
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
            return KeyEvent.Char(KeyEventType.KeyDown, ch.Value, mods);
        }

        _keyDownId[vk] = new CharClassification(null, classified.Key);
        return KeyEvent.Special(KeyEventType.KeyDown, classified.Key!.Value, mods);
    }

    // flush the deferred LCtrl as a real Ctrl key-down event
    private KeyEvent? FlushDeferredLCtrl()
    {
        if (_deferredLCtrl is not { } def) return null;
        _deferredLCtrl = null;
        _keyDownId[def.Vk] = new CharClassification(null, SpecialKey.Control_L);
        return KeyEvent.Special(KeyEventType.KeyDown, SpecialKey.Control_L, def.Mods);
    }

    // combine up to two nullable events into an array (returns null when both are null)
    private static KeyEvent[]? Events(KeyEvent? a, KeyEvent? b)
    {
        if (a is null && b is null) return null;
        if (a is null) return [b!];
        if (b is null) return [a];
        return [a, b];
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
