using System.Runtime.InteropServices;
using Hydra.Keyboard;

namespace Hydra.Platform.MacOs;

// translates raw CGEvent keyboard data into KeyEvents using UCKeyTranslate.
// follows the deskflow/synergy approach: resolve to unicode character on the server side
// so the remote client can reproduce the intended character on its own keyboard layout.
internal sealed class MacKeyResolver
{
    private uint _deadKeyState;
    // CGEventFlags has no ScrollLock bit — this toggle is software-only and cannot be initialised
    // from OS state at startup. it will be wrong if ScrollLock was active before Hydra launched.
    private bool _scrollLockOn;
    private readonly HashSet<int> _pressedModifierVks = [];
    private readonly Dictionary<int, CharClassification> _keyDownId = [];

    private static readonly nint Carbon =
        NativeLibrary.Load("/System/Library/Frameworks/Carbon.framework/Carbon");

    // symbol pointer for kTISPropertyUnicodeKeyLayoutData (loaded once)
    private static readonly nint TisPropertyUnicodeKeyLayoutData =
        Marshal.ReadIntPtr(NativeLibrary.GetExport(Carbon, "kTISPropertyUnicodeKeyLayoutData"));

    internal KeyEvent?[]? Resolve(int eventType, nint eventRef)
    {
        if (eventType == NativeMethods.KCGEventFlagsChanged)
        {
            var ev = ResolveModifierChange(eventRef);
            return ev is not null ? [ev] : null;
        }

        if (eventType is NativeMethods.KCGEventKeyDown or NativeMethods.KCGEventKeyUp)
            return ResolveKeyEvent(eventType, eventRef);

        return null;
    }

    private KeyEvent? ResolveModifierChange(nint eventRef)
    {
        var vkCode = (int)NativeMethods.CGEventGetIntegerValueField(eventRef, NativeMethods.KCGKeyboardEventKeycode);
        var cgFlags = NativeMethods.CGEventGetFlags(eventRef);
        var newMods = MapModifiers(cgFlags);
        if (_scrollLockOn) newMods |= KeyModifiers.ScrollLock;
        if (!MacSpecialKeyMap.Instance.TryGet((ulong)vkCode, out var specialKey)) return null;

        // determine press/release by tracking per-vkCode state, not just generic modifier flags.
        // generic flags can't distinguish L vs R (e.g. Shift_R pressed while Shift_L held keeps Shift set,
        // so changed=0, making the old isPress=(newMods&changed)!=0 logic always wrong in that case).
        var isPress = _pressedModifierVks.Add(vkCode);
        if (!isPress) _pressedModifierVks.Remove(vkCode);

        var type = isPress ? KeyEventType.KeyDown : KeyEventType.KeyUp;
        return KeyEvent.Special(type, specialKey, newMods);
    }

    private KeyEvent?[]? ResolveKeyEvent(int eventType, nint eventRef)
    {
        var vkCode = (int)NativeMethods.CGEventGetIntegerValueField(eventRef, NativeMethods.KCGKeyboardEventKeycode);
        var cgFlags = NativeMethods.CGEventGetFlags(eventRef);
        var mods = MapModifiers(cgFlags);
        if (_scrollLockOn) mods |= KeyModifiers.ScrollLock;

        // key-up: replay the keyId that was emitted on key-down (modifier state may have changed)
        if (eventType == NativeMethods.KCGEventKeyUp)
            return [KeyResolver.ReplayKeyUp(_keyDownId, vkCode, mods)];

        // auto-repeat: re-resolve the held key with current modifier state and forward as a repeat.
        // dead-key state is already consumed, so a composed key (´+e → é) repeats its base char (e); a
        // modifier change mid-hold (Shift) re-resolves the case live (e → E). char resolution stays on the master.
        // detect via the OS autorepeat flag OR our held-key tracking (mirrors input-leap's testAutoRepeat:
        // the flag is authoritative ground truth, tracking is the fallback for repeats the flag doesn't mark).
        var isAutoRepeat = NativeMethods.CGEventGetIntegerValueField(eventRef, NativeMethods.KCGKeyboardEventAutorepeat) == 1;
        if (isAutoRepeat || _keyDownId.ContainsKey(vkCode)) return ResolveRepeat(vkCode, cgFlags, mods);

        // keypad decimal/separator: use UCKeyTranslate for locale-correct char ('.' or ',')
        if ((ulong)vkCode == MacVirtualKey.KeypadDecimal)
        {
            KeyEvent?[]? decDeadFlush = null;
            if (_deadKeyState != 0)
                decDeadFlush = DeadFlushPair();
            _deadKeyState = 0;
            var decEvent = ResolveCharacter(vkCode, cgFlags, mods, ref _deadKeyState);
            if (decEvent is not null)
                _keyDownId[vkCode] = new CharClassification(decEvent.Character, decEvent.Key);
            else
                _keyDownId[vkCode] = new CharClassification(null, null);  // sentinel: ensure key-up is replayed
            if (decDeadFlush is not null) return decEvent is not null ? [.. decDeadFlush, decEvent] : decDeadFlush;
            return decEvent is not null ? [decEvent] : null;
        }

        // special key (function keys, arrows, modifiers, keypad operators)?
        if (MacSpecialKeyMap.Instance.TryGet((ulong)vkCode, out var specialKey))
        {
            KeyEvent?[]? deadFlush = null;
            if (!specialKey.IsModifier())
            {
                // non-modifier special key aborts dead key composition — emit spacing form first.
                // call UCKeyTranslate with Space to resolve the pending dead key to its spacing form
                // (e.g. dead_grave pending → ` emitted before Tab/Esc/arrow).
                if (_deadKeyState != 0)
                    deadFlush = DeadFlushPair();
                _deadKeyState = 0;  // clear in case UCKeyTranslate didn't reset it (dead key with no spacing form)
            }
            // scroll lock (F14) has no CGEventFlag — track toggle in software so the bit travels with every event.
            if (specialKey == SpecialKey.ScrollLock)
            {
                _scrollLockOn = !_scrollLockOn;
                if (_scrollLockOn) mods |= KeyModifiers.ScrollLock; else mods &= ~KeyModifiers.ScrollLock;
            }
            _keyDownId[vkCode] = new CharClassification(null, specialKey);
            var specialEvent = KeyEvent.Special(KeyEventType.KeyDown, specialKey, mods);
            return deadFlush is not null ? [.. deadFlush, specialEvent] : [specialEvent];
        }

        // resolve character via UCKeyTranslate.
        // if a Cmd/Ctrl shortcut would clear a pending dead key inside ResolveCharacter, flush it first
        // so the spacing form is emitted before the shortcut char (dead_grave + Cmd+a → ` then Cmd+a).
        var isCommandChar = (cgFlags & (NativeMethods.KCGEventFlagMaskCommand | NativeMethods.KCGEventFlagMaskControl)) != 0;
        KeyEvent?[]? charDeadFlush = null;
        if (isCommandChar && _deadKeyState != 0)
        {
            charDeadFlush = DeadFlushPair();
            _deadKeyState = 0;
        }
        var ev = ResolveCharacter(vkCode, cgFlags, mods, ref _deadKeyState);
        if (ev is not null)
        {
            _keyDownId[vkCode] = new CharClassification(ev.Character, ev.Key);
        }
        else if (_deadKeyState != 0)
        {
            // dead key consumed — track sentinel so OS auto-repeat events for this vkCode are suppressed.
            // without this, each OS repeat fires UCKeyTranslate again with non-zero dead state, which
            // interprets the repeat as a second dead-key press and emits the spacing form prematurely.
            _keyDownId[vkCode] = new CharClassification(null, null);
        }
        else if (charDeadFlush is not null)
        {
            // pre-flushed a dead key but this key produced no event (e.g. Cmd+dead_key that maps to nothing) —
            // sentinel suppresses OS auto-repeat, which would otherwise re-enter UCKeyTranslate unguarded.
            _keyDownId[vkCode] = new CharClassification(null, null);
        }
        if (charDeadFlush is not null) return ev is not null ? [.. charDeadFlush, ev] : charDeadFlush;
        return ev is not null ? [ev] : null;
    }

    // re-resolves a held key for an OS auto-repeat and emits it marked as a repeat. non-modifier special
    // keys repeat as-is; characters are re-resolved with current modifiers against a throwaway dead-key state
    // (a repeat never composes — the instance _deadKeyState must stay untouched). modifiers don't repeat-type.
    private KeyEvent?[]? ResolveRepeat(int vkCode, ulong cgFlags, KeyModifiers mods)
    {
        if (MacSpecialKeyMap.Instance.TryGet((ulong)vkCode, out var specialKey))
        {
            if (specialKey.IsModifier()) return null;
            return [KeyEvent.Special(KeyEventType.KeyDown, specialKey, mods) with { IsRepeat = true }];
        }

        uint scratchDeadKeyState = 0;
        var ev = ResolveCharacter(vkCode, cgFlags, mods, ref scratchDeadKeyState);
        return ev is not null ? [ev with { IsRepeat = true }] : null;
    }

    // emit current dead key state as [down, up] pair by resolving with Space, then synthesizing the up.
    // the dead key slave has no physical key-up for a flush event, so a standalone down would stick.
    private KeyEvent?[]? DeadFlushPair()
    {
        var downEvent = ResolveCharacter((int)MacVirtualKey.Space, 0, KeyModifiers.None, ref _deadKeyState);
        if (downEvent?.Character is not { } ch) return null;
        return [downEvent, KeyEvent.Char(KeyEventType.KeyUp, ch, KeyModifiers.None)];
    }

    private KeyEvent? ResolveCharacter(int vkCode, ulong cgFlags, KeyModifiers mods, ref uint deadKeyState)
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
            // shortcut context: discard any pending dead key — it would otherwise compose with the shortcut character
            if (isCommand) deadKeyState = 0;

            // include Shift, CapsLock, and Option (when not a Cmd/Ctrl shortcut) in UCKeyTranslate.
            // Hydra resolves the final character server-side, so Option must be included to get
            // e.g. Opt+Shift+4 → '€' rather than '$'. Cmd/Ctrl shortcuts strip both Option AND Shift
            // so the base key character is sent (e.g. '4' not '¤' for Cmd+Shift+4 on a Norwegian keyboard).
            // The slave receives Shift in mods and reconstructs the correct VK + Shift injection.
            uint ucMods = 0;
            if (!isCommand && (cgFlags & NativeMethods.KCGEventFlagMaskShift) != 0) ucMods |= 0x02;      // shiftKey >> 8
            if ((cgFlags & NativeMethods.KCGEventFlagMaskAlphaShift) != 0) ucMods |= 0x04;               // alphaLock >> 8
            if (!isCommand && (cgFlags & NativeMethods.KCGEventFlagMaskAlternate) != 0) ucMods |= 0x08;  // optionKey >> 8

            CharClassification classified;
            unsafe
            {
                ushort* chars = stackalloc ushort[2];
                // kUCKeyTranslateNoDeadKeysBit=1: prevent Cmd/Ctrl+dead_key from activating dead-key state.
                // without this, pressing Cmd+` (dead_grave on some layouts) leaves _deadKeyState non-zero,
                // so the next character press (e.g. 'a') composes 'à' instead of 'a'.
                var status = NativeMethods.UCKeyTranslate(
                    layoutPtr,
                    (ushort)(vkCode & 0xFFu),
                    NativeMethods.KUCKeyActionDown,
                    ucMods,
                    NativeMethods.LMGetKbdType(),
                    isCommand ? 1u : 0u,
                    ref deadKeyState,
                    2,
                    out var count,
                    chars);

                if (status != 0) return null;

                // count=0 with non-zero dead state means a dead key was consumed; next keypress will compose.
                // deadKeyState intentionally not cleared here — it's needed for composition on the next keypress.
                if (count == 0 && deadKeyState != 0) return null;

                deadKeyState = 0;
                if (count == 0) return null;

                classified = KeyResolver.ClassifyChar((char)chars[0]);
            }

            if (!classified.Ch.HasValue && !classified.Key.HasValue) return null;

            // detect AltGr: option was held and produced a printable character (not a keyboard shortcut)
            bool optionHeld = (cgFlags & NativeMethods.KCGEventFlagMaskAlternate) != 0;
            // Option+Space is commonly used as an application shortcut on macOS. UCKeyTranslate renders
            // it as a non-breaking space, which would otherwise be treated as AltGr text and injected as
            // Unicode on the slave. Preserve the physical Space key and Option modifier so shortcut
            // recorders and handlers receive the real chord; normal text entry still produces the
            // destination layout's Option+Space character.
            if (optionHeld && !isCommand && (ulong)vkCode == MacVirtualKey.Space)
                return KeyEvent.Char(KeyEventType.KeyDown, ' ', mods);
            if (DetectAltGr(classified.Ch, isCommand, optionHeld))
            {
                mods |= KeyModifiers.AltGr;
                mods &= ~(KeyModifiers.Alt | KeyModifiers.Shift); // AltGr and Alt are mutually exclusive; Shift consumed by level-3 resolution
            }

            if (classified.Ch.HasValue) return KeyEvent.Char(KeyEventType.KeyDown, classified.Ch.Value, mods);
            return KeyEvent.Special(KeyEventType.KeyDown, classified.Key!.Value, mods);
        }
        finally
        {
            NativeMethods.CFRelease(layoutSource);
        }
    }

    // resets composition and press-tracking state after the event tap is re-enabled.
    // missed key-up events while the tap was disabled leave stale entries in _pressedModifierVks
    // and _keyDownId; clearing them prevents phantom held-key state.
    // _scrollLockOn is intentionally NOT reset — it is a persistent lock state (like CapsLock),
    // not per-grab transient state. missed events during the tap gap cannot corrupt it.
    internal void Reset()
    {
        _deadKeyState = 0;
        _pressedModifierVks.Clear();
        _keyDownId.Clear();
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
        // KCGEventFlagMaskNumericPad is a source flag ("event came from numpad"), not a NumLock state indicator.
        // macOS has no traditional NumLock; mapping this flag to KeyModifiers.NumLock causes spurious NumLock
        // bits on every numpad keystroke regardless of lock state.
        return mods;
    }

    // detects whether option acted as AltGr: option was held, the key produced a printable character,
    // and no command modifier was held (which would make it a keyboard shortcut instead).
    internal static bool DetectAltGr(char? character, bool isCommand, bool optionHeld) =>
        optionHeld && !isCommand && character.HasValue;

}
