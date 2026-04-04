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
    private readonly Dictionary<int, (char? ch, SpecialKey? key)> _keyDownId = [];

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
        if (!MacSpecialKeyMap.Instance.TryGet((ulong)vkCode, out var specialKey)) return null;

        var isPress = (newMods & changed) != 0;
        var type = isPress ? KeyEventType.KeyDown : KeyEventType.KeyUp;
        return KeyEvent.Special(type, specialKey, newMods);
    }

    private KeyEvent? ResolveKeyEvent(int eventType, nint eventRef)
    {
        var vkCode = (int)NativeMethods.CGEventGetIntegerValueField(eventRef, NativeMethods.KCGKeyboardEventKeycode);
        var cgFlags = NativeMethods.CGEventGetFlags(eventRef);
        var mods = MapModifiers(cgFlags);

        // key-up: replay the keyId that was emitted on key-down (modifier state may have changed)
        if (eventType == NativeMethods.KCGEventKeyUp)
            return KeyResolver.ReplayKeyUp(_keyDownId, vkCode, mods);

        // special key (function keys, arrows, modifiers, keypad)?
        if (MacSpecialKeyMap.Instance.TryGet((ulong)vkCode, out var specialKey))
        {
            _deadKeyState = 0;
            _keyDownId[vkCode] = (null, specialKey);
            return KeyEvent.Special(KeyEventType.KeyDown, specialKey, mods);
        }

        // resolve character via UCKeyTranslate
        var ev = ResolveCharacter(vkCode, cgFlags, mods);
        if (ev is not null)
        {
            _keyDownId[vkCode] = (ev.Character, ev.Key);
        }
        return ev;
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

            // include Shift, CapsLock, and Option (when not a Cmd/Ctrl shortcut) in UCKeyTranslate.
            // Hydra resolves the final character server-side, so Option must be included to get
            // e.g. Opt+Shift+4 → '€' rather than '$'. Cmd/Ctrl shortcuts still strip Option.
            uint ucMods = 0;
            if ((cgFlags & NativeMethods.KCGEventFlagMaskShift) != 0) ucMods |= 0x02;       // shiftKey >> 8
            if ((cgFlags & NativeMethods.KCGEventFlagMaskAlphaShift) != 0) ucMods |= 0x04;  // alphaLock >> 8
            if (!isCommand && (cgFlags & NativeMethods.KCGEventFlagMaskAlternate) != 0) ucMods |= 0x08;  // optionKey >> 8

            char? ch;
            SpecialKey? specialKey;
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

                (ch, specialKey) = KeyResolver.ClassifyChar((char)chars[0]);
            }

            if (!ch.HasValue && !specialKey.HasValue) return null;

            // detect AltGr: option was held and produced a printable character (not a keyboard shortcut)
            bool optionHeld = (cgFlags & NativeMethods.KCGEventFlagMaskAlternate) != 0;
            if (DetectAltGr(ch, isCommand, optionHeld))
                mods |= KeyModifiers.AltGr;

            if (ch.HasValue) return KeyEvent.Char(KeyEventType.KeyDown, ch.Value, mods);
            return KeyEvent.Special(KeyEventType.KeyDown, specialKey!.Value, mods);
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

    // detects whether option acted as AltGr: option was held, the key produced a printable character,
    // and no command modifier was held (which would make it a keyboard shortcut instead).
    internal static bool DetectAltGr(char? character, bool isCommand, bool optionHeld) =>
        optionHeld && !isCommand && character.HasValue;

    private static readonly nint _carbon =
        NativeLibrary.Load("/System/Library/Frameworks/Carbon.framework/Carbon");

    private static nint LoadTisPropertyKey() =>
        Marshal.ReadIntPtr(NativeLibrary.GetExport(_carbon, "kTISPropertyUnicodeKeyLayoutData"));
}
