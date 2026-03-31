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

    private readonly Dictionary<int, (char? ch, SpecialKey? key)> _keyDownId = [];

    // pending dead key combining character (e.g. '\u0301' for acute accent)
    private char _pendingDeadKey;
    private char _pendingDeadSpacing;  // spacing form used when composition fails (e.g. dead_circumflex + space → ^)

    internal KeyEvent? Resolve(int wParam, KBDLLHOOKSTRUCT info)
    {
        var vk = (int)info.vkCode;
        var isKeyUp = wParam is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;

        // update tracked key state before resolving
        UpdateKeyState(vk, info.flags, isKeyUp);

        // suppress injected modifier events (e.g. synthetic LCtrl from AltGr — state is tracked
        // but the event itself should not be forwarded to the receiver)
        if ((info.flags & NativeMethods.LLKHF_INJECTED) != 0 && IsModifier(vk)) return null;

        var mods = GetModifiers();

        if (isKeyUp)
        {
            _keyDownId.Remove(vk, out var downVal);
            if (downVal.ch.HasValue) return KeyEvent.Char(KeyEventType.KeyUp, downVal.ch.Value, mods);
            if (downVal.key.HasValue) return KeyEvent.Special(KeyEventType.KeyUp, downVal.key.Value, mods);
            return null;
        }

        if (WinSpecialKeyMap.TryGet(vk, out var specialKey))
        {
            // suppress modifier auto-repeat — only emit on initial press, not while held
            if (specialKey.IsModifier() && _keyDownId.ContainsKey(vk)) return null;
            _pendingDeadKey = '\0';
            _pendingDeadSpacing = '\0';
            _keyDownId[vk] = (null, specialKey);
            return KeyEvent.Special(KeyEventType.KeyDown, specialKey, mods);
        }

        return ResolveCharacter(vk, info.scanCode, info.flags, mods);
    }

    private void UpdateKeyState(int vk, uint flags, bool isKeyUp)
    {
        // ignore injected keys for modifier state (avoids phantom ctrl from AltGr synthesis)
        bool injected = (flags & NativeMethods.LLKHF_INJECTED) != 0;
        if (injected && IsModifier(vk)) return;

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
        // AltGr = RMenu alone; the synthesized LCtrl is injected and stripped in UpdateKeyState
        if ((_keyState[WinVirtualKey.RMenu] & 0x80) != 0) mods |= KeyModifiers.AltGr;
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
        _resolveState[WinVirtualKey.LWin] = 0;
        _resolveState[WinVirtualKey.RWin] = 0;

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
                    var combining = DeadCharToCombining(spacingForm);
                    if (combining != '\0')
                    {
                        _pendingDeadKey = combining;
                        _pendingDeadSpacing = spacingForm;
                    }
                    return null;
                }

                rawChar = buff[0];
            }
        }

        var (ch, key) = KeyResolver.ClassifyChar(rawChar);
        if (!ch.HasValue && !key.HasValue) return null;

        // apply pending dead key composition
        if (_pendingDeadKey != '\0')
        {
            var dead = _pendingDeadKey;
            var spacing = _pendingDeadSpacing;
            _pendingDeadKey = '\0';
            _pendingDeadSpacing = '\0';

            if (ch.HasValue)
            {
                if (ch.Value == ' ' && spacing != '\0')
                    ch = spacing;   // dead key + space → spacing form (e.g. dead_circumflex + space → ^)
                else
                {
                    var composed = KeyResolver.Compose(ch.Value, dead);
                    if (composed != ch.Value) ch = composed;
                    // else: incompatible pair — emit base char, dead key lost
                }
            }
        }

        if (ch.HasValue)
        {
            _keyDownId[vk] = (ch, null);
            return KeyEvent.Char(KeyEventType.KeyDown, ch.Value, mods);
        }

        _keyDownId[vk] = (null, key);
        return KeyEvent.Special(KeyEventType.KeyDown, key!.Value, mods);
    }

    // maps the standalone dead key character from ToUnicodeEx buff[0] to its Unicode combining equivalent.
    // standalone dead chars (spacing accents) must be converted to combining chars for NFC composition.
    private static char DeadCharToCombining(char dead) => dead switch
    {
        '`' => '\u0300',  // grave accent
        '\u00B4' => '\u0301',  // acute accent (U+00B4)
        '^' => '\u0302',  // circumflex
        '~' => '\u0303',  // tilde
        '\u00AF' => '\u0304',  // macron (U+00AF overline/macron)
        '\u02D8' => '\u0306',  // breve
        '\u02D9' => '\u0307',  // dot above
        '\u00A8' => '\u0308',  // diaeresis / umlaut (U+00A8)
        '\u02DA' => '\u030A',  // ring above
        '\u02DD' => '\u030B',  // double acute
        '\u02C7' => '\u030C',  // caron
        '\u00B8' => '\u0327',  // cedilla (U+00B8)
        '\u02DB' => '\u0328',  // ogonek
        _ => '\0',
    };

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
