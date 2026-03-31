using Hydra.Keyboard;

namespace Hydra.Platform.Windows;

// translates low-level keyboard hook data into KeyEvents using ToUnicodeEx.
// follows the same approach as MacKeyResolver: strip ctrl/win for base character,
// detect AltGr separately (right alt held + printable glyph).
internal sealed class WinKeyResolver
{
    // full key state array (256 bytes): high bit set = pressed, low bit = toggled (for lock keys)
    private readonly byte[] _keyState = new byte[256];

    // scratch buffer for ToUnicodeEx — avoid allocation on each keypress
    private readonly byte[] _resolveState = new byte[256];

    private readonly Dictionary<int, (char? ch, SpecialKey? key)> _keyDownId = [];

    internal KeyEvent? Resolve(int wParam, KBDLLHOOKSTRUCT info)
    {
        var vk = (int)info.vkCode;
        var isKeyUp = wParam is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;

        // update tracked key state before resolving
        UpdateKeyState(vk, info.flags, isKeyUp);

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
            _keyDownId[vk] = (null, specialKey);
            return KeyEvent.Special(KeyEventType.KeyDown, specialKey, mods);
        }

        return ResolveCharacter(vk, info.scanCode, mods);
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

            // toggle lock keys on keydown
            if (vk == WinVirtualKey.Capital || vk == WinVirtualKey.Numlock || vk == WinVirtualKey.Scroll)
                _keyState[vk] ^= 0x01;
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
        // right alt acts as AltGr on many European layouts
        if (_keyState[WinVirtualKey.RMenu] != 0) mods |= KeyModifiers.AltGr;
        return mods;
    }

    private KeyEvent? ResolveCharacter(int vk, uint scanCode, KeyModifiers mods)
    {
        // build resolve state: strip ctrl and win unless AltGr (ctrl+rmenu) is active.
        // this gives the base character for Ctrl+X combos (e.g. Ctrl+A → 'a' not 0x01).
        Array.Copy(_keyState, _resolveState, 256);

        bool altGrActive = _keyState[WinVirtualKey.RMenu] != 0;
        if (!altGrActive)
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

        char? ch;
        SpecialKey? specialKey;
        unsafe
        {
            char* buff = stackalloc char[4];
            fixed (byte* pState = _resolveState)
            {
                var count = NativeMethods.ToUnicodeEx((uint)vk, scanCode, pState, buff, 4, 0, hkl);

                // count < 0: dead key consumed (no output yet); count == 0: no character
                if (count <= 0) return null;

                (ch, specialKey) = ClassifyChar(buff[0]);
            }
        }

        if (!ch.HasValue && !specialKey.HasValue) return null;

        if (ch.HasValue)
        {
            _keyDownId[vk] = (ch, null);
            return KeyEvent.Char(KeyEventType.KeyDown, ch.Value, mods);
        }

        _keyDownId[vk] = (null, specialKey);
        return KeyEvent.Special(KeyEventType.KeyDown, specialKey!.Value, mods);
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

    // classifies a unicode char from ToUnicodeEx output as a printable char or SpecialKey.
    // control characters are mapped to their SpecialKey constants; other printable chars pass through.
    internal static (char? ch, SpecialKey? key) ClassifyChar(char c) => c switch
    {
        (char)3 => (null, SpecialKey.KP_Enter),
        (char)8 => (null, SpecialKey.BackSpace),
        (char)9 => (null, SpecialKey.Tab),
        (char)13 => (null, SpecialKey.Return),
        (char)27 => (null, SpecialKey.Escape),
        (char)127 => (null, SpecialKey.Delete),
        _ when c < 32 => (null, null),
        _ => (c, null),
    };

    private static bool IsModifier(int vk) =>
        vk is WinVirtualKey.LShift or WinVirtualKey.RShift
            or WinVirtualKey.LControl or WinVirtualKey.RControl
            or WinVirtualKey.LMenu or WinVirtualKey.RMenu
            or WinVirtualKey.LWin or WinVirtualKey.RWin;
}
