using System.Runtime.InteropServices;
using Hydra.Keyboard;

namespace Hydra.Platform.Linux;

// translates evdev keycodes into platform-independent KeyEvents using libxkbcommon.
// libxkbcommon produces X11-compatible keysyms, so all keysym→KeyEvent mapping from
// XorgKeyResolver is reused directly — only the source of keysyms differs.
internal sealed class EvdevKeyResolver : IDisposable
{
    private readonly nint _ctx;
    private readonly nint _keymap;
    private nint _state;
    private char _pendingDeadKey;
    private char _pendingDeadSpacing;
    private readonly Dictionary<uint, CharClassification> _keyDownId = [];

    internal EvdevKeyResolver(string layout)
    {
        _ctx = EvdevNativeMethods.xkb_context_new(0);
        if (_ctx == 0) throw new InvalidOperationException("Failed to create xkb context.");

        // allocate native strings for rule names; freed in Dispose
        var names = new XkbRuleNames
        {
            Rules = Marshal.StringToCoTaskMemUTF8("evdev"),
            Model = Marshal.StringToCoTaskMemUTF8("pc105"),
            Layout = Marshal.StringToCoTaskMemUTF8(layout),
            Variant = 0,
            Options = 0,
        };

        try
        {
            _keymap = EvdevNativeMethods.xkb_keymap_new_from_names(_ctx, ref names, 0);
        }
        finally
        {
            Marshal.FreeCoTaskMem(names.Rules);
            Marshal.FreeCoTaskMem(names.Model);
            Marshal.FreeCoTaskMem(names.Layout);
        }

        if (_keymap == 0) throw new InvalidOperationException($"Failed to create xkb keymap for layout '{layout}'.");

        _state = EvdevNativeMethods.xkb_state_new(_keymap);
        if (_state == 0) throw new InvalidOperationException("Failed to create xkb state.");
    }

    // value=1 → KeyDown, value=0 → KeyUp, value=2 → repeat (ignored — auto-repeat handled by slave)
    internal KeyEvent?[]? Resolve(uint evdevCode, int value)
    {
        if (value == 2) return null;   // evdev auto-repeat: slave handles locally

        // evdev keycode + 8 = xkb keycode (X11 convention used by libxkbcommon)
        var xkbKey = evdevCode + 8;
        var isDown = value == 1;

        // key-up: replay the character that was recorded on key-down.
        // update state first so modifier key-up reports the post-release modifier state.
        if (!isDown)
        {
            _ = EvdevNativeMethods.xkb_state_update_key(_state, xkbKey, EvdevNativeMethods.XKB_KEY_UP);
            var mods = GetModifiers();
            return [KeyResolver.ReplayKeyUp(_keyDownId, evdevCode, mods)];
        }

        // resolve keysym BEFORE updating state (xkbcommon convention: keysym must not be affected by the event itself)
        var keysym = (ulong)EvdevNativeMethods.xkb_state_key_get_one_sym(_state, xkbKey);
        _ = EvdevNativeMethods.xkb_state_update_key(_state, xkbKey, EvdevNativeMethods.XKB_KEY_DOWN);
        if (keysym == 0) return null;

        // when Super or Control is held (shortcut context), override to the base keysym (level 0) so
        // shortcut keys send their unshifted character (e.g. '4' not '¤' on Norwegian for Super+Shift+4).
        if (IsModActive("Mod4") || IsModActive("Control"))
        {
            var layout = EvdevNativeMethods.xkb_state_serialize_layout(_state, EvdevNativeMethods.XKB_STATE_LAYOUT_EFFECTIVE);
            var count = EvdevNativeMethods.xkb_keymap_key_get_syms_by_level(_keymap, xkbKey, layout, 0, out var symsPtr);
            if (count > 0 && symsPtr != nint.Zero)
                keysym = (uint)Marshal.ReadInt32(symsPtr);
            // if the base key is a dead key, emit its spacing form (e.g. Ctrl+` → `) so the shortcut
            // fires with the correct base char. dead keys with no spacing form are dropped.
            if (XorgKeyResolver.DeadKeyLookup(keysym) is { Combining: not '\0' } deadShortcut)
            {
                // only clear pending dead key if we're actually emitting a replacement event.
                // dropping (no spacing form) must leave pending state intact.
                if (deadShortcut.Spacing == '\0') return null;
                // flush any prior pending dead key: dead_grave pending + Ctrl+dead_acute → ` then Ctrl+´
                var prevFlush = XorgKeyResolver.TakeDeadKeySpacing(ref _pendingDeadKey, ref _pendingDeadSpacing);
                _keyDownId[evdevCode] = new CharClassification(deadShortcut.Spacing, null);
                var shortcutEvent = KeyEvent.Char(KeyEventType.KeyDown, deadShortcut.Spacing, GetModifiers());
                return prevFlush is not null ? [prevFlush, shortcutEvent] : [shortcutEvent];
            }
        }

        // keypad dual-purpose keys: normalize based on numlock state.
        // xkbcommon applies KEYPAD XOR semantics (numlock XOR shift → level), but we want
        // simple numlock-on=chars, numlock-off=navigation regardless of shift.
        if (IsModActive("Mod2"))
        {
            // numlock on: ensure we have the numeric keysym.
            // xkbcommon XOR may give a nav keysym (e.g. Shift+numpad-decimal with numlock on).
            // re-query at shift level to get the layout-correct numeric keysym instead of hard-coding.
            if (keysym is >= 0xFF95 and <= 0xFF9F)
            {
                var layout = EvdevNativeMethods.xkb_state_serialize_layout(_state, EvdevNativeMethods.XKB_STATE_LAYOUT_EFFECTIVE);
                var cnt = EvdevNativeMethods.xkb_keymap_key_get_syms_by_level(_keymap, xkbKey, layout, 1, out var symsPtr);
                keysym = cnt > 0 && symsPtr != nint.Zero
                    ? (uint)Marshal.ReadInt32(symsPtr)
                    : XorgKeyResolver.KpNavToChar(keysym);
            }
            keysym = XorgKeyResolver.KpNumericToChar(keysym);
        }
        else
        {
            // numlock off: emit standard navigation; also handle xkbcommon XOR numeric → nav
            if (keysym is >= 0xFFB0 and <= 0xFFB9 || keysym == XorgVirtualKey.KP_Decimal || keysym == 0xFFAC)
                keysym = XorgKeyResolver.KpNumericToNav(keysym);  // shift+numlock-off XOR gave numeric; remap to nav
            keysym = XorgKeyResolver.MapKpNavToStandard(keysym);
        }

        var downMods = GetModifiers();
        var type = KeyEventType.KeyDown;

        // pre-flush: non-modifier special key while dead key pending — emit spacing form before the special key
        var deadFlush = XorgKeyResolver.FlushDeadKeyBeforeSpecial(keysym, ref _pendingDeadKey, ref _pendingDeadSpacing);

        // evdev needs a placeholder _keyDownId entry for dead keys to support key-up replay
        var ev = XorgKeyResolver.ResolveKeysym(keysym, evdevCode, _keyDownId,
            ref _pendingDeadKey, ref _pendingDeadSpacing, downMods, type, trackDeadKey: true);
        return deadFlush is not null ? [deadFlush, ev] : ev is not null ? [ev] : null;
    }

    private KeyModifiers GetModifiers()
    {
        var mods = KeyModifiers.None;
        if (IsModActive("Shift")) mods |= KeyModifiers.Shift;
        if (IsModActive("Lock")) mods |= KeyModifiers.CapsLock;
        if (IsModActive("Control")) mods |= KeyModifiers.Control;
        if (IsModActive("Mod1")) mods |= KeyModifiers.Alt;
        if (IsModActive("Mod2")) mods |= KeyModifiers.NumLock;
        if (IsModActive("Mod4")) mods |= KeyModifiers.Super;
        if (IsModActive("Mod5")) mods |= KeyModifiers.AltGr;
        // AltGr and Alt are mutually exclusive: strip Alt when AltGr is active
        if ((mods & KeyModifiers.AltGr) != 0) mods &= ~KeyModifiers.Alt;
        return mods;
    }

    private bool IsModActive(string name) =>
        EvdevNativeMethods.xkb_state_mod_name_is_active(_state, name, EvdevNativeMethods.XKB_STATE_MODS_EFFECTIVE) > 0;

    // resets resolver state when the evdev grab is re-established after a gap.
    // missed key-up events leave stale _keyDownId entries; clearing prevents phantom stuck-key suppression.
    // xkb state is also recreated: stale modifier bits (e.g. Shift held when focus switched away) would
    // bleed into new events via GetModifiers() / IsModActive() if the state object is reused.
    internal void Reset()
    {
        _pendingDeadKey = '\0';
        _pendingDeadSpacing = '\0';
        _keyDownId.Clear();
        if (_state != 0) EvdevNativeMethods.xkb_state_unref(_state);
        _state = EvdevNativeMethods.xkb_state_new(_keymap);
    }

    public void Dispose()
    {
        if (_state != 0) EvdevNativeMethods.xkb_state_unref(_state);
        if (_keymap != 0) EvdevNativeMethods.xkb_keymap_unref(_keymap);
        if (_ctx != 0) EvdevNativeMethods.xkb_context_unref(_ctx);
    }
}
