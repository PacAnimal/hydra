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
    private readonly nint _state;
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
    internal KeyEvent? Resolve(uint evdevCode, int value)
    {
        if (value == 2) return null;   // evdev auto-repeat: slave handles locally

        // evdev keycode + 8 = xkb keycode (X11 convention used by libxkbcommon)
        var xkbKey = evdevCode + 8;
        var isDown = value == 1;

        // key-up: replay the character that was recorded on key-down
        if (!isDown)
        {
            var mods = GetModifiers();
            _ = EvdevNativeMethods.xkb_state_update_key(_state, xkbKey, EvdevNativeMethods.XKB_KEY_UP);
            return KeyResolver.ReplayKeyUp(_keyDownId, evdevCode, mods);
        }

        // resolve keysym BEFORE updating state (xkbcommon convention: keysym must not be affected by the event itself)
        var keysym = (ulong)EvdevNativeMethods.xkb_state_key_get_one_sym(_state, xkbKey);
        _ = EvdevNativeMethods.xkb_state_update_key(_state, xkbKey, EvdevNativeMethods.XKB_KEY_DOWN);
        if (keysym == 0) return null;

        // keypad dual-purpose keys: normalize based on numlock state.
        // xkbcommon applies KEYPAD XOR semantics (numlock XOR shift → level), but we want
        // simple numlock-on=chars, numlock-off=navigation regardless of shift.
        if (IsModActive("Mod2"))
        {
            // numlock on: emit digits/decimal; also handle xkbcommon XOR nav → char
            if (keysym is >= 0xFFB0 and <= 0xFFB9)
                keysym = (ulong)('0' + (keysym - 0xFFB0));
            else if (keysym == XorgVirtualKey.KP_Decimal)
                keysym = '.';
            else if (keysym is >= 0xFF95 and <= 0xFF9F)
                keysym = XorgKeyResolver.KpNavToChar(keysym);  // shift+numlock XOR gave nav; remap to char
        }
        else
        {
            // numlock off: emit standard navigation; also handle xkbcommon XOR numeric → nav
            if (keysym is >= 0xFFB0 and <= 0xFFB9 || keysym == XorgVirtualKey.KP_Decimal)
                keysym = XorgKeyResolver.KpNumericToNav(keysym);  // shift+numlock-off XOR gave numeric; remap to nav
            keysym = XorgKeyResolver.MapKpNavToStandard(keysym);
        }

        var downMods = GetModifiers();
        var type = KeyEventType.KeyDown;

        // evdev needs a placeholder _keyDownId entry for dead keys to support key-up replay
        return XorgKeyResolver.ResolveKeysym(keysym, evdevCode, _keyDownId,
            ref _pendingDeadKey, ref _pendingDeadSpacing, downMods, type, trackDeadKey: true);
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
        return mods;
    }

    private bool IsModActive(string name) =>
        EvdevNativeMethods.xkb_state_mod_name_is_active(_state, name, EvdevNativeMethods.XKB_STATE_MODS_EFFECTIVE) > 0;

    public void Dispose()
    {
        if (_state != 0) EvdevNativeMethods.xkb_state_unref(_state);
        if (_keymap != 0) EvdevNativeMethods.xkb_keymap_unref(_keymap);
        if (_ctx != 0) EvdevNativeMethods.xkb_context_unref(_ctx);
    }
}
