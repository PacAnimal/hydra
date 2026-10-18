using System.Runtime.InteropServices;
using Cathedral.Utils;
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
    // Resolve() runs on the evdev event-loop thread; Reset()/Dispose() run on other threads (the
    // InputRouter consumer sets IsOnVirtualScreen → Reset). Reset/Dispose free+recreate the native
    // xkb_state, so serialize all access to avoid a use-after-free / torn _keyDownId.
    private readonly Lock _stateLock = new();
    private readonly Toggle _disposed = new();
    private char _pendingDeadKey;
    private char _pendingDeadSpacing;
    private readonly Dictionary<uint, CharClassification> _keyDownId = [];
    // scroll lock modifier name — "ScrollLock" (evdev/pc105 keymap) or "Mod3" (fallback)
    private readonly string _scrollLockModName;
    // xkb keycodes for lock keys looked up by name at init; 0 if not present in the keymap
    private readonly uint _capsLockXkbKey;
    private readonly uint _numLockXkbKey;
    private readonly uint _scrollLockXkbKey;

    internal EvdevKeyResolver(LinuxInputConfig.XkbNames xkb)
    {
        var layout = xkb.Layout;
        _ctx = EvdevNativeMethods.xkb_context_new(0);
        if (_ctx == 0) throw new InvalidOperationException("Failed to create xkb context.");

        // allocate native strings for rule names; freed in the finally below.
        // Model comes from the system config too: a 105-key board is the common case but not a
        // universal one, and getting it wrong misplaces the keys either side of Enter.
        var names = new XkbRuleNames
        {
            Rules = Marshal.StringToCoTaskMemUTF8("evdev"),
            Model = Marshal.StringToCoTaskMemUTF8(xkb.Model),
            Layout = Marshal.StringToCoTaskMemUTF8(layout),
            Variant = xkb.Variant is null ? 0 : Marshal.StringToCoTaskMemUTF8(xkb.Variant),
            Options = xkb.Options is null ? 0 : Marshal.StringToCoTaskMemUTF8(xkb.Options),
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
            if (names.Variant != 0) Marshal.FreeCoTaskMem(names.Variant);
            if (names.Options != 0) Marshal.FreeCoTaskMem(names.Options);
        }

        if (_keymap == 0) throw new InvalidOperationException($"Failed to create xkb keymap for layout '{layout}'.");

        // detect whether the keymap names ScrollLock as a virtual modifier; fall back to Mod3
        _scrollLockModName = EvdevNativeMethods.xkb_keymap_mod_get_index(_keymap, "ScrollLock") != uint.MaxValue
            ? "ScrollLock" : "Mod3";
        // look up xkb keycodes for lock keys by standard XKB name; 0 if not in keymap
        var capsKey = EvdevNativeMethods.xkb_keymap_key_by_name(_keymap, "CAPS");
        _capsLockXkbKey = capsKey != uint.MaxValue ? capsKey : 0;
        var nmlkKey = EvdevNativeMethods.xkb_keymap_key_by_name(_keymap, "NMLK");
        _numLockXkbKey = nmlkKey != uint.MaxValue ? nmlkKey : 0;
        var sclkKey = EvdevNativeMethods.xkb_keymap_key_by_name(_keymap, "SCLK");
        _scrollLockXkbKey = sclkKey != uint.MaxValue ? sclkKey : 0;

        _state = EvdevNativeMethods.xkb_state_new(_keymap);
        if (_state == 0) throw new InvalidOperationException("Failed to create xkb state.");
    }

    // value=1 → KeyDown, value=0 → KeyUp, value=2 → auto-repeat
    internal KeyEvent?[]? Resolve(uint evdevCode, int value)
    {
        KeyEvent?[]? events;
        lock (_stateLock) events = ResolveLocked(evdevCode, value);
        KeyResolver.StampPhysicalKey(events, (int)evdevCode);
        return events;
    }

    private KeyEvent?[]? ResolveLocked(uint evdevCode, int value)
    {
        if (_disposed) return null;
        // evdev keycode + 8 = xkb keycode (X11 convention used by libxkbcommon)
        var xkbKey = evdevCode + 8;

        // auto-repeat: re-resolve the held key with current modifier state and forward as a repeat.
        // dead-key state is already consumed, so a composed key (¨+e → ë) repeats its base char (e); a
        // modifier change mid-hold re-resolves the case live (e → E). char resolution stays on the master.
        if (value == 2) return ResolveRepeat(xkbKey);

        var isDown = value == 1;

        // key-up: update xkb state FIRST, then read modifiers.
        // xkbcommon state reflects held keys; after updating for key-up the released key is no
        // longer marked held. GetModifiers() must see post-release state so that, e.g., a Shift
        // key-up carries modifiers WITHOUT Shift — the slave uses these mods to track its own
        // modifier state. reading mods before the update sends stale pre-release state, causing
        // the slave to think the released modifier is still held. DO NOT swap this order.
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

        keysym = NormalizeKeypadKeysym(xkbKey, ShortcutBaseKeysym(xkbKey, keysym));
        return XorgKeyResolver.ResolveDown(keysym, evdevCode, IsShortcut(), GetModifiers(), _keyDownId, ref _pendingDeadKey, ref _pendingDeadSpacing);
    }

    // re-resolves the held key's current keysym (without updating xkb state) and emits a repeat event.
    // applies the same shortcut-base and keypad normalization as the initial press so the repeated char
    // tracks live modifier state; dead-key state is untouched (a repeat uses the key's base char).
    private KeyEvent?[]? ResolveRepeat(uint xkbKey)
    {
        var keysym = (ulong)EvdevNativeMethods.xkb_state_key_get_one_sym(_state, xkbKey);
        if (keysym == 0) return null;
        keysym = ShortcutBaseKeysym(xkbKey, keysym);
        keysym = NormalizeKeypadKeysym(xkbKey, keysym);
        return XorgKeyResolver.BuildRepeat(keysym, GetModifiers());
    }

    // Super or Control held
    private bool IsShortcut() => IsModActive("Mod4") || IsModActive("Control");

    // a shortcut takes the level-0 base keysym, so it sends the unshifted char ('4' not '¤' on Norwegian for Super+Shift+4)
    private ulong ShortcutBaseKeysym(uint xkbKey, ulong keysym)
    {
        if (!IsShortcut()) return keysym;
        var layout = EvdevNativeMethods.xkb_state_serialize_layout(_state, EvdevNativeMethods.XKB_STATE_LAYOUT_EFFECTIVE);
        var count = EvdevNativeMethods.xkb_keymap_key_get_syms_by_level(_keymap, xkbKey, layout, 0, out var symsPtr);
        if (count > 0 && symsPtr != nint.Zero)
            keysym = (uint)Marshal.ReadInt32(symsPtr);
        return keysym;
    }

    // keypad dual-purpose keys: normalize based on numlock state.
    // xkbcommon applies KEYPAD XOR semantics (numlock XOR shift → level), but we want
    // simple numlock-on=chars, numlock-off=navigation regardless of shift.
    private ulong NormalizeKeypadKeysym(uint xkbKey, ulong keysym)
    {
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
            keysym = XorgKeyResolver.KpDecimalToChar(keysym);
        }
        else
        {
            // numlock off: emit standard navigation; also handle xkbcommon XOR numeric → nav
            if (XorgKeyResolver.IsKpNumeric(keysym))
                keysym = XorgKeyResolver.KpNumericToNav(keysym);  // shift+numlock-off XOR gave numeric; remap to nav
            keysym = XorgKeyResolver.MapKpNavToStandard(keysym);
        }
        return keysym;
    }

    private KeyModifiers GetModifiers()
    {
        var mods = KeyModifiers.None;
        if (IsModActive("Shift")) mods |= KeyModifiers.Shift;
        if (IsModActive("Lock")) mods |= KeyModifiers.CapsLock;
        if (IsModActive("Control")) mods |= KeyModifiers.Control;
        if (IsModActive("Mod1")) mods |= KeyModifiers.Alt;
        if (IsModActive("Mod2")) mods |= KeyModifiers.NumLock;
        if (IsModActive(_scrollLockModName)) mods |= KeyModifiers.ScrollLock;
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
        lock (_stateLock) ResetLocked();
    }

    private void ResetLocked()
    {
        if (_disposed) return;
        _pendingDeadKey = '\0';
        _pendingDeadSpacing = '\0';
        _keyDownId.Clear();
        var capsLockWasActive = IsModActive("Lock");  // "Lock" is the XKB virtual modifier name for CapsLock
        var numLockWasActive = IsModActive("Mod2");
        var scrollLockWasActive = IsModActive(_scrollLockModName);
        if (_state != 0) EvdevNativeMethods.xkb_state_unref(_state);
        _state = EvdevNativeMethods.xkb_state_new(_keymap);
        // xkb_state_new() starts all locks off — restore any that were active via down+up simulation.
        if (_state != 0)
        {
            if (_capsLockXkbKey != 0 && capsLockWasActive) RestoreLock(_capsLockXkbKey);
            if (_numLockXkbKey != 0 && numLockWasActive) RestoreLock(_numLockXkbKey);
            if (_scrollLockXkbKey != 0 && scrollLockWasActive) RestoreLock(_scrollLockXkbKey);
        }
    }

    private void RestoreLock(uint xkbKey)
    {
        _ = EvdevNativeMethods.xkb_state_update_key(_state, xkbKey, EvdevNativeMethods.XKB_KEY_DOWN);
        _ = EvdevNativeMethods.xkb_state_update_key(_state, xkbKey, EvdevNativeMethods.XKB_KEY_UP);
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            // one-shot: block any later Resolve/Reset (e.g. from a shutdown-race) from touching freed state
            if (!_disposed.TrySet()) return;
            if (_state != 0) { EvdevNativeMethods.xkb_state_unref(_state); _state = 0; }
            if (_keymap != 0) EvdevNativeMethods.xkb_keymap_unref(_keymap);
            if (_ctx != 0) EvdevNativeMethods.xkb_context_unref(_ctx);
        }
    }
}
