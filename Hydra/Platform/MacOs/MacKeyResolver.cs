using System.Runtime.InteropServices;
using Hydra.Keyboard;

namespace Hydra.Platform.MacOs;

// translates raw CGEvent keyboard data into KeyEvents using UCKeyTranslate.
// follows the deskflow/synergy approach: resolve to unicode character on the server side
// so the remote client can reproduce the intended character on its own keyboard layout.
internal sealed class MacKeyResolver
{
    private uint _deadKeyState;
    private KeyModifiers _previousModifiers;

    // symbol pointer for kTISPropertyUnicodeKeyLayoutData (loaded once)
    private static readonly nint TisPropertyUnicodeKeyLayoutData = LoadTisPropertyKey();

    internal KeyEvent? Resolve(int eventType, nint eventRef)
    {
        if (eventType == NativeMethods.KCGEventFlagsChanged)
            return ResolveModifierChange(eventRef);

        if (eventType is NativeMethods.KCGEventKeyDown or NativeMethods.KCGEventKeyUp)
            return ResolveKeyEvent(eventType, eventRef);

        return null;
    }

    private KeyEvent? ResolveModifierChange(nint eventRef)
    {
        var vkCode = (int)NativeMethods.CGEventGetIntegerValueField(eventRef, NativeMethods.KCGKeyboardEventKeycode);
        var cgFlags = NativeMethods.CGEventGetFlags(eventRef);
        var newMods = MapModifiers(cgFlags);

        var changed = _previousModifiers ^ newMods;
        _previousModifiers = newMods;

        if (changed == KeyModifiers.None) return null;
        if (!MacSpecialKeyMap.TryGet(vkCode, out var keyId)) return null;

        var isPress = (newMods & changed) != 0;
        var type = isPress ? KeyEventType.KeyDown : KeyEventType.KeyUp;
        return new KeyEvent(type, keyId, newMods, (ushort)(vkCode + 1));
    }

    private KeyEvent? ResolveKeyEvent(int eventType, nint eventRef)
    {
        var vkCode = (int)NativeMethods.CGEventGetIntegerValueField(eventRef, NativeMethods.KCGKeyboardEventKeycode);
        var cgFlags = NativeMethods.CGEventGetFlags(eventRef);
        var mods = MapModifiers(cgFlags);

        // key-up: no character resolution needed — just return the physical key
        if (eventType == NativeMethods.KCGEventKeyUp)
        {
            _deadKeyState = 0;
            if (!MacSpecialKeyMap.TryGet(vkCode, out var upId)) upId = KeyId.None;
            return new KeyEvent(KeyEventType.KeyUp, upId, mods, (ushort)(vkCode + 1));
        }

        // special key (function keys, arrows, modifiers, keypad)?
        if (MacSpecialKeyMap.TryGet(vkCode, out var specialId))
        {
            _deadKeyState = 0;
            return new KeyEvent(KeyEventType.KeyDown, specialId, mods, (ushort)(vkCode + 1));
        }

        // resolve character via UCKeyTranslate
        return ResolveCharacter(vkCode, cgFlags, mods);
    }

    private KeyEvent? ResolveCharacter(int vkCode, ulong cgFlags, KeyModifiers mods)
    {
        var layoutSource = NativeMethods.TISCopyCurrentKeyboardLayoutInputSource();
        if (layoutSource == nint.Zero) return null;

        try
        {
            var layoutData = NativeMethods.TISGetInputSourceProperty(layoutSource, TisPropertyUnicodeKeyLayoutData);
            if (layoutData == nint.Zero) return null;

            var layoutPtr = NativeMethods.CFDataGetBytePtr(layoutData);
            if (layoutPtr == nint.Zero) return null;

            // is this a command (ctrl or cmd held)? used for AltGr detection
            bool isCommand = (cgFlags & (NativeMethods.KCGEventFlagMaskCommand | NativeMethods.KCGEventFlagMaskControl)) != 0;

            // strip command, control, and option from modifiers before UCKeyTranslate
            // (deskflow: always strips option to get the base glyph, then uses AltGr detection)
            uint ucMods = 0;
            if ((cgFlags & NativeMethods.KCGEventFlagMaskShift) != 0) ucMods |= 0x02;       // shiftKey >> 8
            if ((cgFlags & NativeMethods.KCGEventFlagMaskAlphaShift) != 0) ucMods |= 0x04;  // alphaLock >> 8

            uint keyId;
            unsafe
            {
                ushort* chars = stackalloc ushort[2];
                var status = NativeMethods.UCKeyTranslate(
                    layoutPtr,
                    (ushort)(vkCode & 0xFFu),
                    NativeMethods.KUCKeyActionDown,
                    ucMods,
                    NativeMethods.LMGetKbdType(),
                    0,
                    ref _deadKeyState,
                    2,
                    out var count,
                    chars);

                if (status != 0) return null;

                // count=0 with non-zero dead state means a dead key was consumed; next keypress will compose
                if (count == 0 && _deadKeyState != 0) return null;

                _deadKeyState = 0;
                if (count == 0) return null;

                keyId = UnicodeToKeyId((char)chars[0]);
            }

            if (keyId == KeyId.None) return null;

            // detect AltGr: option was held and produced a printable glyph (not a keyboard shortcut)
            bool optionHeld = (cgFlags & NativeMethods.KCGEventFlagMaskAlternate) != 0;
            if (DetectAltGr(keyId, isCommand, optionHeld))
                mods |= KeyModifiers.AltGr;

            return new KeyEvent(KeyEventType.KeyDown, keyId, mods, (ushort)(vkCode + 1));
        }
        finally
        {
            NativeMethods.CFRelease(layoutSource);
        }
    }

    // maps CGEventFlags to the platform-independent KeyModifiers bitmask.
    // command (macOS) maps to Super (cross-platform); option maps to Alt.
    internal static KeyModifiers MapModifiers(ulong cgFlags)
    {
        var mods = KeyModifiers.None;
        if ((cgFlags & NativeMethods.KCGEventFlagMaskShift) != 0) mods |= KeyModifiers.Shift;
        if ((cgFlags & NativeMethods.KCGEventFlagMaskControl) != 0) mods |= KeyModifiers.Control;
        if ((cgFlags & NativeMethods.KCGEventFlagMaskAlternate) != 0) mods |= KeyModifiers.Alt;
        if ((cgFlags & NativeMethods.KCGEventFlagMaskCommand) != 0) mods |= KeyModifiers.Super;
        if ((cgFlags & NativeMethods.KCGEventFlagMaskAlphaShift) != 0) mods |= KeyModifiers.CapsLock;
        if ((cgFlags & NativeMethods.KCGEventFlagMaskNumericPad) != 0) mods |= KeyModifiers.NumLock;
        return mods;
    }

    // detects whether option acted as AltGr: option was held, the key produced a printable glyph,
    // and no command modifier was held (which would make it a keyboard shortcut instead).
    internal static bool DetectAltGr(uint keyId, bool isCommand, bool optionHeld) =>
        optionHeld && !isCommand && KeyId.IsPrintable(keyId);

    // converts a unicode char from UCKeyTranslate output to a KeyId.
    // control characters are mapped to their special KeyId constants.
    internal static uint UnicodeToKeyId(char c) => c switch
    {
        (char)3 => KeyId.KP_Enter,
        (char)8 => KeyId.BackSpace,
        (char)9 => KeyId.Tab,
        (char)13 => KeyId.Return,
        (char)27 => KeyId.Escape,
        (char)127 => KeyId.Delete,
        _ when c < 32 => KeyId.None,
        _ => c,
    };

    private static nint LoadTisPropertyKey()
    {
        var lib = NativeLibrary.Load("/System/Library/Frameworks/Carbon.framework/Carbon");
        var export = NativeLibrary.GetExport(lib, "kTISPropertyUnicodeKeyLayoutData");
        return Marshal.ReadIntPtr(export);
    }
}
